using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ZoneSnapper.Engine
{
    /// <summary>
    /// 自管的行式 key=value 存储（用于记住「哪些区域是本模组描出来的、它的手动节点是哪些」，
    /// 让需求 5 的已提交区域跟随能跨会话工作）。
    ///
    /// 这个类的每一条规则都对应 Playbook §3.2 里一次真实返工，写在这里而不是靠记性：
    ///  · 读盘必须**抗缺键**：缺键返回 ""，绝不返回 null —— 读中途抛异常会让内存变成
    ///    「前半套读到的 + 后半套默认值」的混合体，下一次写盘就把默认值整份盖进文件，玩家存过的值永久丢失；
    ///  · 读不全 ⇒ <see cref="LoadIncomplete"/> ⇒ 拒绝写盘（宁可这一局的改动不落盘）；
    ///  · 空值永不覆盖非空值（读写两侧同一判据）；
    ///  · 写盘键集合是读盘键集合的真子集时，「缺键」是常态不是异常 ⇒ 本类把「期望键」显式登记；
    ///  · 自己拼 JSON/文本就得自己保证引号成对（一颗漏闭合引号会让读回来的值带尾逗号）。
    /// </summary>
    public sealed class StoreKit
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> _expected = new HashSet<string>(StringComparer.Ordinal);

        public string LastError;
        public bool LoadIncomplete { get; private set; }
        public int LineCount { get; private set; }

        /// <summary>登记「这份文件正常时应该有哪些键」。缺任何一个 ⇒ LoadIncomplete。</summary>
        public void ExpectKey(string key)
        {
            if (!string.IsNullOrEmpty(key)) _expected.Add(key);
        }

        public bool Has(string key)
        {
            return key != null && _values.ContainsKey(key);
        }

        /// <summary>缺键返回 ""（不是 null）。调用方拿到的永远是可用字符串。</summary>
        public string Get(string key)
        {
            string v;
            if (key == null) return string.Empty;
            return _values.TryGetValue(key, out v) ? v : string.Empty;
        }

        public double GetDouble(string key, double fallback)
        {
            string s = Get(key);
            if (s.Length == 0) return fallback;
            double d;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return fallback;
            if (double.IsNaN(d) || double.IsInfinity(d)) return fallback;
            return d;
        }

        public int GetInt(string key, int fallback)
        {
            string s = Get(key);
            if (s.Length == 0) return fallback;
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        public bool GetBool(string key, bool fallback)
        {
            string s = Get(key);
            if (s.Length == 0) return fallback;
            if (s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (s == "0" || string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        /// <summary>空值永不覆盖非空值（读写两侧同一口径 —— Playbook §3.2）。</summary>
        public void Set(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (string.IsNullOrEmpty(value))
            {
                // 口径落地点：这里**不写空**。老实现会把已有非空值抹成空串，
                // 于是「玩家存过的值」在一次误触发的空写入后永久丢失（回归壳 Z4 抓到，
                // 正是这个类开头承诺要防的那条）。
                return;
            }
            _values[key] = SanitizeValue(value);
        }

        /// <summary>
        /// 真要抹掉一个键走这里。空值写入按 Z4 的口径是**不生效**的，所以「删除」必须有显式出口，
        /// 否则调用方只能靠 <c>Set(key, "")</c> 表达删除 —— 那条路被上一条规则挡死了。
        /// </summary>
        public void Remove(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            _values.Remove(key);
        }

        public void SetDouble(string key, double v)
        {
            Set(key, v.ToString("R", CultureInfo.InvariantCulture));
        }

        public void SetInt(string key, int v)
        {
            Set(key, v.ToString(CultureInfo.InvariantCulture));
        }

        public void SetBool(string key, bool v)
        {
            Set(key, v ? "1" : "0");
        }

        /// <summary>
        /// 解析。容忍：# 注释、BOM、CRLF、行内空格、重复键（后者覆盖前者）、无 '=' 的行（跳过并计数）。
        /// 绝不在这里抛异常 —— 这个文件一旦被玩家或旧版本写坏，也不能让模组启动失败。
        ///
        /// 两条口径同时成立，靠的是「键出现但值为空」与「键压根没出现」的区别：
        ///  · 键**没出现** ⇒ 本次结果里就没有这个键（覆盖语义，Z2：删掉的键不能阴魂不散写回去）；
        ///  · 键出现但**值为空** ⇒ 保留内存里已有的非空值（Z4：旧版本/半行截断留下的空等号不算删除指令）。
        /// 先解析进一张临时表再整体换掉 <c>_values</c>，中途出任何岔子都不会把原表清成半套
        /// （Playbook §3.2：「读盘读到一半」+「下一次写盘」= 玩家存过的值永久丢失）。
        /// </summary>
        public void Load(string text)
        {
            LastError = null;
            LineCount = 0;
            if (text == null)
            {
                // 读不出来 ≠ 玩家删了数据：表原样留着，只按期望键判完整性。
                LoadIncomplete = !AllExpectedPresent();
                return;
            }
            if (text.Length > 0 && (int)text[0] == 0xFEFF) text = text.Substring(1);   // UTF-8 BOM：外部严格解析器要先剥（Playbook §3.5）

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            Dictionary<string, string> next = new Dictionary<string, string>(StringComparer.Ordinal);
            int malformed = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line == null) continue;
                string t = line.Trim();
                if (t.Length == 0 || t[0] == '#') continue;
                int eq = t.IndexOf('=');
                if (eq <= 0)
                {
                    malformed++;
                    continue;
                }
                string key = t.Substring(0, eq).Trim();
                if (key.Length == 0) { malformed++; continue; }
                string val = Unquote(t.Substring(eq + 1));
                if (val.Length == 0)
                {
                    // 读到空 ⇒ 保持原值：本文件前面已给过真值就用它，否则回落到内存里的旧值。
                    if (next.ContainsKey(key)) continue;
                    string prev;
                    if (_values.TryGetValue(key, out prev) && prev.Length > 0) next[key] = prev;
                    continue;
                }
                next[key] = val;
                LineCount++;
            }
            _values.Clear();
            foreach (KeyValuePair<string, string> kv in next) _values[kv.Key] = kv.Value;
            if (malformed > 0) LastError = malformed + " malformed line(s)";
            LoadIncomplete = !AllExpectedPresent();
        }

        public bool AllExpectedPresent()
        {
            foreach (string k in _expected)
            {
                if (!_values.ContainsKey(k)) return false;
            }
            return true;
        }

        /// <summary>
        /// 序列化。<see cref="LoadIncomplete"/> 时返回 null —— 调用方据此拒写。
        /// 排序输出 ⇒ 同样内容同样字节，便于「变了没」的比对与测试断言。
        /// </summary>
        public string Save()
        {
            if (LoadIncomplete) return null;
            List<string> keys = new List<string>(_values.Keys);
            keys.Sort(StringComparer.Ordinal);
            StringBuilder sb = new StringBuilder();
            sb.Append("# ZoneSnapper state\n");
            sb.Append("# entries=").Append(keys.Count).Append('\n');
            for (int i = 0; i < keys.Count; i++)
            {
                sb.Append(keys[i]).Append('=').Append(Quote(_values[keys[i]])).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// 值归一化：去掉首尾空白与成对引号，重复一次即不变（幂等）。
        /// 幂等很重要：绑定的路径串会被「读→改→写→再读」反复过这个函数，
        /// 不幂等就会每次少一截（Playbook 里那颗尾逗号的 bug 就是这么来的）。
        /// </summary>
        public static string SanitizeValue(string raw)
        {
            if (raw == null) return string.Empty;
            string s = raw.Trim();
            // 剥掉内部的控制字符：中文输入法会混进不可见控制字符（Playbook §3.5）。
            StringBuilder sb = null;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool bad = (int)c < 32 || (int)c == 0x7F;
                if (bad)
                {
                    if (sb == null)
                    {
                        sb = new StringBuilder(s.Length);
                        sb.Append(s, 0, i);
                    }
                    continue;
                }
                if (sb != null) sb.Append(c);
            }
            if (sb != null) s = sb.ToString().Trim();
            return s;
        }

        private static string Unquote(string v)
        {
            string s = SanitizeValue(v);
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"') s = s.Substring(1, s.Length - 2);
            return SanitizeValue(s);
        }

        private static string Quote(string v)
        {
            string s = SanitizeValue(v);
            bool need = s.Length == 0 || s.IndexOf(' ') >= 0;
            return need ? "\"" + s + "\"" : s;
        }
    }
}
