using System;
using System.Collections.Generic;
using ZoneSnapper.Engine;

namespace ZoneSnapper.T3
{
    internal static partial class Tests
    {
        /// <summary>
        /// StoreKit：这个类存在的理由就是 Playbook §3.2 那几次真实返工——
        /// 缺键读出 null 会抛 ArgumentNullException；读一半就写盘会把没读到的键整份盖成默认值，
        /// 玩家存过的设置永久丢失。所以这里测的全是「坏输入不许毁掉好数据」。
        /// </summary>
        internal static class Store
        {
            public static void Run()
            {
                Harness.Section("StoreKit 缺键安全读（Z）", () =>
                {
                    var st = new StoreKit();
                    Check.Str("Z 缺键 Get 返回空串而不是 null", "", st.Get("nope"));
                    Check.Str("Z 未 Load 过的表 Get 也是空串", "", st.Get("anything"));
                    Check.Str("Z key=null ⇒ 空串", "", st.Get(null));
                    Check.True("Z 任何路径都不返回 null（Playbook §3.2 那条 ArgumentNullException）", () =>
                        st.Get("a") != null && st.Get("") != null && st.Get("中文键") != null);
                    Check.False("Z Has 能区分「没有这个键」", () => st.Has("nope"));
                    Check.Close("Z 缺键 GetDouble 走 fallback", 3.5, st.GetDouble("nope", 3.5), 1e-12);
                    Check.Int("Z 缺键 GetInt 走 fallback", -7, st.GetInt("nope", -7));
                    Check.Bool("Z 缺键 GetBool 走 fallback", true, st.GetBool("nope", true));

                    st.Load("a=1\nb=2.5\nc=true\nd=0\n");
                    Check.Int("Z 读回整数", 1, st.GetInt("a", 0));
                    Check.Close("Z 读回小数（区域倍率就是这个小数）", 2.5, st.GetDouble("b", 0), 1e-12);
                    Check.Bool("Z 读回 true", true, st.GetBool("c", false));
                    Check.Bool("Z 读回 0 ⇒ false", false, st.GetBool("d", true));
                    Check.Str("Z 存在的键 Has 为真", "2.5", st.Get("b"));
                    Check.True("Z Has 对存在的键为真", () => st.Has("a"));
                    // 垃圾值/空值的一组探针必须用**新表**：Z4 的口径是「读到空 ⇒ 保留已有非空值」，
                    // 拿上面那张装着 b=2.5 的表去验「空值读回来是空串」，验的其实是继承行为而不是空值。
                    var junky = new StoreKit();
                    junky.Load("a=notANumber\nb=\n");
                    Check.Close("Z 值不是数字 ⇒ fallback（绝不抛 FormatException）", 9, junky.GetDouble("a", 9), 1e-12);
                    Check.Int("Z 值是垃圾 ⇒ GetInt fallback", 8, junky.GetInt("a", 8));
                    Check.Bool("Z 空值 ⇒ GetBool fallback", true, junky.GetBool("b", true));
                    Check.Str("Z 空值读回来是空串而不是 null（新表：无旧值可继承）", "", junky.Get("b"));
                    Check.True("Z 空值读回来绝不吐 null", junky.Get("b") != null);
                    Check.False("Z 空值 + 无旧值可继承 ⇒ 不落键（要清掉请走 Remove）", () => junky.Has("b"));
                    st.Load("a=NaN\nb=Infinity\n");
                    Check.Close("Z NaN 值 ⇒ fallback（NaN 会让整个策略层瘫掉）", 4, st.GetDouble("a", 4), 1e-12);
                    Check.Close("Z Infinity 值 ⇒ fallback", 4, st.GetDouble("b", 4), 1e-12);
                    Check.True("Z 但 Get 仍原样给出字符串（读盘不篡改）", () => st.Get("a") == "NaN");
                });

                Harness.Section("StoreKit 读不全拒绝写盘（Z2）", () =>
                {
                    var st = new StoreKit();
                    st.ExpectKey("schema");
                    st.ExpectKey("areas");
                    st.ExpectKey("nodes");
                    Check.False("Z2 空表 + 期望键 ⇒ AllExpectedPresent 为假", () => st.AllExpectedPresent());

                    // 只有前半：典型的中途截断（存档写到一半掉电 / 玩家手改了一半）
                    st.Load("schema=1\nareas=42\n");
                    Check.True("Z2 截断文本 ⇒ LoadIncomplete", () => st.LoadIncomplete);
                    Check.Null("Z2 LoadIncomplete ⇒ Save() 返回 null（调用方据此拒写，绝不覆盖好档）", st.Save());

                    // 全是垃圾
                    var junk = new StoreKit();
                    junk.ExpectKey("schema");
                    junk.Load("<<<<<<<not a store>>>>>>>\n\t\t\n===\n");
                    Check.True("Z2 纯垃圾 ⇒ LoadIncomplete", () => junk.LoadIncomplete);
                    Check.Null("Z2 纯垃圾 ⇒ 拒绝写盘", junk.Save());
                    Check.True("Z2 垃圾文本不抛异常，且记了 LastError", () => !string.IsNullOrEmpty(junk.LastError));

                    // null / 空文本
                    var nul = new StoreKit();
                    nul.ExpectKey("schema");
                    nul.Load(null);
                    Check.True("Z2 Load(null) ⇒ LoadIncomplete（新档与坏档不能混为一谈）", () => nul.LoadIncomplete);
                    Check.Null("Z2 Load(null) ⇒ 拒绝写盘", nul.Save());
                    var fresh = new StoreKit();
                    fresh.Load(null);
                    Check.False("Z2 没登记期望键时 null 视为「全新空档」，可以写", () => fresh.LoadIncomplete);
                    Check.Str("Z2 全新空档 Save() 出的是可用的头两行", "# ZoneSnapper state\n# entries=0\n", fresh.Save());

                    // 补全之后必须能正常写盘（LoadIncomplete 不能被上一次失败粘住）
                    st.Load("schema=1\nareas=42\nnodes=7\n");
                    Check.False("Z2 补全后 LoadIncomplete 归零", () => st.LoadIncomplete);
                    Check.True("Z2 补全后 Save() 给出非空文本", () => !string.IsNullOrEmpty(st.Save()));
                    Check.Str("Z2 补全后 nodes 读得回来", "7", st.Get("nodes"));

                    // 覆盖性：一次 Load 必须清掉上一次的残留，否则「删掉的键」会阴魂不散地写回去
                    var over = new StoreKit();
                    over.Load("a=1\nb=2\n");
                    over.Load("a=9\n");
                    Check.Str("Z2 第二次 Load 覆盖了第一次（旧键不再存在）", "", over.Get("b"));
                    Check.Str("Z2 新值生效", "9", over.Get("a"));
                });

                Harness.Section("StoreKit 往返与字节稳定（Z3）", () =>
                {
                    var st = new StoreKit();
                    st.ExpectKey("schema");
                    st.SetInt("schema", 1);
                    st.SetBool("snap.district", true);
                    st.SetBool("snap.lot", false);
                    st.SetDouble("radius.mult", 1.5);
                    st.Set("label", "工业区 A 段");                 // 带空格
                    st.Set("empty.ok", "");                         // 空值：不该覆盖非空
                    st.Set("empty.first", "");                      // 空值：也不该凭空写一个空键
                    st.Set("dup", "v1");
                    st.Set("dup", "v2");                            // 后写覆盖先写
                    var text = st.Save();
                    Check.True("Z3 Save 非 null", () => !string.IsNullOrEmpty(text));
                    Check.Str("Z3 同一份内容两次 Save 字节完全相同（排序输出）", text, st.Save());
                    Check.True("Z3 输出按 Ordinal 排序（键序稳定 ⇒ 能做「变了没」的比对）", () =>
                    {
                        var again = st.Save();
                        var l1 = again.Split('\n');
                        var keys = new List<string>();
                        foreach (string s in l1)
                        {
                            if (s.Length == 0 || s[0] == '#') continue;
                            int eq = s.IndexOf('=');
                            if (eq > 0) keys.Add(s.Substring(0, eq));
                        }
                        var sorted = new List<string>(keys);
                        sorted.Sort(StringComparer.Ordinal);
                        if (keys.Count != sorted.Count) return false;
                        for (int i = 0; i < keys.Count; i++) if (keys[i] != sorted[i]) return false;
                        return keys.Count == 6;
                    });

                    var back = new StoreKit();
                    back.ExpectKey("schema");
                    back.Load(text);
                    Check.False("Z3 往返后不缺键", () => back.LoadIncomplete);
                    Check.Int("Z3 整数往返", 1, back.GetInt("schema", 0));
                    Check.Bool("Z3 true 往返", true, back.GetBool("snap.district", false));
                    Check.Bool("Z3 false 往返（不能被当成缺键 fallback）", false, back.GetBool("snap.lot", true));
                    Check.Close("Z3 小数往返（InvariantCulture，中文系统也必须读得回）", 1.5, back.GetDouble("radius.mult", 0), 1e-12);
                    Check.Str("Z3 带空格的值原样回来（不能被截成「工业区」）", "工业区 A 段", back.Get("label"));
                    Check.Str("Z3 后写覆盖先写", "v2", back.Get("dup"));
                    Check.False("Z3 空值没被写成新键", () => back.Has("empty.first"));
                    Check.Str("Z3 二次往返仍稳定", text, back.Save());

                    // 解析容忍度：BOM / CRLF / 注释 / 重复键 / 无 '=' 的行 / 行内空格
                    var messy = new StoreKit();
                    messy.Load("\uFEFF# 抬头注释\r\n  spaced =  abc  \r\n\r\ndup=1\ndup=2\r\nnodashline\r\n=xnokey\r\ntrail=1");
                    Check.Str("Z3 BOM+CRLF+空格 都能解析", "abc", messy.Get("spaced"));
                    Check.Str("Z3 重复键以后者为准", "2", messy.Get("dup"));
                    Check.Str("Z3 行尾无换行也读得到", "1", messy.Get("trail"));
                    Check.Int("Z3 垃圾行被跳过并计数（无'='的一行 + 空键名的一行）", 2, CountBad(messy.LastError));
                    Check.False("Z3 没登记期望键 ⇒ 垃圾行不算 LoadIncomplete", () => messy.LoadIncomplete);

                    // 值里含 '=' 与 '#'：必须整值保留
                    var eqv = new StoreKit();
                    eqv.Set("expr", "a=b=c");
                    eqv.Load(eqv.Save());
                    Check.Str("Z3 值里的 '=' 不被当成第二个分隔符", "a=b=c", eqv.Get("expr"));
                    var hashv = new StoreKit();
                    hashv.Set("c", "1#2");
                    hashv.Load(hashv.Save());
                    Check.Str("Z3 值里的 '#' 不被当成注释", "1#2", hashv.Get("c"));

                    // 空键名 / null 值都不能污染表
                    var guard = new StoreKit();
                    guard.Set(null, "x");
                    guard.Set("", "x");
                    guard.Set("k", null);
                    Check.Int("Z3 空键与 null 值都被忽略（表里 0 项）", 0, CountEntries(guard.Save()));
                });

                Harness.Section("StoreKit 空值不许覆盖非空值（Z4）", () =>
                {
                    // 这就是 §3.2 那条规则的本体：读盘读到空串（旧版本没写这个键 / 玩家删了半行）
                    // 时必须保留已有的非空值，否则下一次 Save 就把玩家的设置抹成空了。
                    var st = new StoreKit();
                    st.Set("k", "玩家存的值");
                    st.Set("k", "");
                    Check.Str("Z4 空值不覆盖已有非空值（Set 侧）", "玩家存的值", st.Get("k"));
                    var st2 = new StoreKit();
                    st2.Set("k", "玩家存的值");
                    var text = st2.Save();
                    st2.Load(text.Replace("k=玩家存的值", "k="));
                    Check.Str("Z4 空值不覆盖已有非空值（Load 侧：读到空 ⇒ 保持原值）", "玩家存的值", st2.Get("k"));
                    var st3 = new StoreKit();
                    st3.Set("k", "v");
                    st3.Set("k", null);
                    Check.Str("Z4 null 也不覆盖", "v", st3.Get("k"));
                    // 「删除」的正当出口：Set("") 被口径挡死，那就必须有 Remove，
                    // 否则调用方唯一的办法是绕过自己的规则去直改字典 —— 那这条口径就成了摆设。
                    st3.Remove("k");
                    Check.False("Z4 Remove 之后键真的没了", () => st3.Has("k"));
                    st3.Load("k=\n");
                    Check.Str("Z4 Remove 之后再读到空 ⇒ 空着（没有旧值可继承）", "", st3.Get("k"));
                    st3.Set("keep", "abc");
                    st3.Remove("nope");
                    Check.Str("Z4 Remove 不存在的键不抛、也不影响别的键", "abc", st3.Get("keep"));
                    Check.True("Z4 Remove(null) 不抛", () => { st3.Remove(null); return true; });
                });

                Harness.Section("StoreKit SanitizeValue（Z5）", () =>
                {
                    Check.Str("Z5 null ⇒ 空串", "", StoreKit.SanitizeValue(null));
                    Check.Str("Z5 首尾空白被去掉", "abc", StoreKit.SanitizeValue("  abc \t"));
                    Check.Str("Z5 剥掉 NUL/0x01/换行等控制符", "ab", StoreKit.SanitizeValue("a\u0000b\u000A"));
                    Check.Str("Z5 剥掉 0x7F（DEL 也是控制符，中文输入法会混进来）", "ab", StoreKit.SanitizeValue("a\u007Fb"));
                    Check.Str("Z5 制表符 U+0009 被剥（值里不许留裸制表）", "ab", StoreKit.SanitizeValue("a\tb"));
                    Check.Str("Z5 中文与常规标点原样保留", "工业区（一期）", StoreKit.SanitizeValue("工业区（一期）"));
                    Check.Str("Z5 只有控制符 ⇒ 空串", "", StoreKit.SanitizeValue("\u0001\u0002\u007F"));
                    bool idem = true;
                    string[] probes =
                    {
                        "  abc  ", "a\u0001b", "a\u007F\u007Fb", "\t\u000A x \u000D", "\"q\"", "工业区 A 段",
                        "\u0000", "", "  ", "a b  c", "x=", "#c", "\uFEFF"
                    };
                    for (int i = 0; i < probes.Length; i++)
                    {
                        string once = StoreKit.SanitizeValue(probes[i]);
                        string twice = StoreKit.SanitizeValue(once);
                        if (once != twice)
                        {
                            Check.Bad("Z5 幂等性被破坏（第二次变了）", "一次=" + Check.Show(once), "两次=" + Check.Show(twice));
                            idem = false;
                        }
                    }
                    Check.True("Z5 对全部 " + probes.Length + " 个探针 SanitizeValue(SanitizeValue(x)) == SanitizeValue(x)", () => idem);
                    Check.Str("Z5 Set 内部就做了归一", "ab", (new StoreKit().SetAndPeek("k", "  a\u0000b  ")));
                });
            }

            private static int CountBad(string lastError)
            {
                if (string.IsNullOrEmpty(lastError)) return 0;
                int sp = lastError.IndexOf(' ');
                int v;
                return sp > 0 && int.TryParse(lastError.Substring(0, sp), out v) ? v : 0;
            }

            private static int CountEntries(string saveText)
            {
                if (saveText == null) return -1;
                int n = 0;
                foreach (string s in saveText.Split('\n'))
                {
                    if (s.Length == 0 || s[0] == '#') continue;
                    if (s.IndexOf('=') > 0) n++;
                }
                return n;
            }
        }
    }

    /// <summary>给测试用的小扩展：不改 Engine 一行代码，只加一个读回手段。</summary>
    internal static class StoreKitTestExt
    {
        public static string SetAndPeek(this StoreKit st, string key, string value)
        {
            st.Set(key, value);
            return st.Get(key);
        }
    }
}
