using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ZoneSnapper.T3
{
    /// <summary>
    /// 极简断言台：只做「计数 + 打一行 + 决定退出码」这一件事，不做花活。
    /// 形状沿用兄弟模组 BridgeTheLanguageGap 的 T3 壳（同一套门禁脚本要能同样地解析它）。
    ///
    /// 设计口径：
    ///  · 一条断言一行，PASS/FAIL 前缀固定，方便 verify.mjs 用正则抓总数；
    ///  · 失败必须带「期望/实际」，实机调参的人不看代码也能读懂；
    ///  · 断言本身抛异常 = 这条 FAIL（不是整个壳崩），因为「引擎在某个输入下抛」
    ///    与「引擎算错了」是同等重要的缺陷，绝不能因为一条崩掉就丢掉后面几百条的信息。
    /// </summary>
    internal static class Check
    {
        private static readonly List<string> Failures = new List<string>();
        private static int _pass;
        private static int _fail;
        private static int _total;

        public static int Pass => _pass;
        public static int Fail => _fail;
        public static int Total => _total;

        /// <summary>当前所属断言组（Section 设置），失败清单里带上它便于定位。</summary>
        public static string Group = "";

        public static void Ok(string name)
        {
            _total++; _pass++;
            Console.WriteLine("  PASS  " + name);
        }

        public static void Bad(string name, string detail)
        {
            _total++; _fail++;
            Failures.Add("[" + Group + "] " + name + "   →   " + detail);
            Console.WriteLine("  FAIL  " + name + "   →   " + detail);
        }

        public static void Bad(string name, string expected, string actual)
        {
            Bad(name, "期望 " + Show(expected) + " 实际 " + Show(actual));
        }

        /// <summary>期望为真的断言。条件求值异常也算 FAIL。</summary>
        public static void True(string name, Func<bool> cond)
        {
            bool ok;
            try { ok = cond(); }
            catch (Exception e) { Bad(name, "断言求值抛异常 " + Short(e)); return; }
            if (ok) Ok(name); else Bad(name, "true", "false");
        }

        public static void True(string name, bool actual)
        {
            if (actual) Ok(name); else Bad(name, "true", "false");
        }

        public static void False(string name, Func<bool> cond)
        {
            bool ok;
            try { ok = cond(); }
            catch (Exception e) { Bad(name, "断言求值抛异常 " + Short(e)); return; }
            if (!ok) Ok(name); else Bad(name, "false", "true");
        }

        public static void Bool(string name, bool expected, bool actual)
        {
            if (expected == actual) Ok(name);
            else Bad(name, expected ? "true" : "false", actual ? "true" : "false");
        }

        public static void Int(string name, int expected, int actual)
        {
            if (expected == actual) Ok(name);
            else Bad(name, expected.ToString(CultureInfo.InvariantCulture), actual.ToString(CultureInfo.InvariantCulture));
        }

        public static void Long(string name, long expected, long actual)
        {
            if (expected == actual) Ok(name);
            else Bad(name, expected.ToString(CultureInfo.InvariantCulture), actual.ToString(CultureInfo.InvariantCulture));
        }

        public static void Str(string name, string expected, string actual)
        {
            if (string.Equals(expected, actual, StringComparison.Ordinal)) Ok(name);
            else Bad(name, expected ?? "<null>", actual ?? "<null>");
        }

        /// <summary>断言为 null（StoreKit 用 null 当「拒绝写盘」的信号，这里必须是 null 而不是空串）。</summary>
        public static void Null(string name, string actual)
        {
            if (actual == null) Ok(name);
            else Bad(name, "<null>", Show(actual));
        }

        public static void NotNull(string name, object actual)
        {
            if (actual != null) Ok(name);
            else Bad(name, "非 null", "<null>");
        }

        /// <summary>空集合断言（null 也算不通过：引擎层的约定是「返回空表而非 null」）。</summary>
        public static void Empty<T>(string name, List<T> l)
        {
            if (l != null && l.Count == 0) Ok(name);
            else Bad(name, "空集合", l == null ? "<null>" : l.Count + " 项");
        }

        public static void NotEmpty<T>(string name, List<T> l)
        {
            if (l != null && l.Count > 0) Ok(name);
            else Bad(name, "非空集合", l == null ? "<null>" : "0 项");
        }

        /// <summary>浮点等值：绝对容差。几何断言全部走这条。</summary>
        public static void Close(string name, double expected, double actual, double tol)
        {
            bool ok = !double.IsNaN(actual) && !double.IsNaN(expected) && System.Math.Abs(expected - actual) <= tol;
            if (ok) Ok(name);
            else Bad(name, Fmt(expected), Fmt(actual) + " (偏差 " + Fmt(System.Math.Abs(actual - expected)) + " > " + Fmt(tol) + ")");
        }

        /// <summary>断言 actual 落在 [lo, hi] 闭区间内（含端点）。</summary>
        public static void InRange(string name, double actual, double lo, double hi)
        {
            if (!double.IsNaN(actual) && actual >= lo && actual <= hi) Ok(name);
            else Bad(name, Fmt(lo) + " .. " + Fmt(hi), Fmt(actual));
        }

        /// <summary>断言 actual 严格落在 (lo, hi) 开区间内。</summary>
        public static void InOpenRange(string name, double actual, double lo, double hi)
        {
            if (!double.IsNaN(actual) && actual > lo && actual < hi) Ok(name);
            else Bad(name, "(" + Fmt(lo) + ", " + Fmt(hi) + ")", Fmt(actual));
        }

        public static void Enum<T>(string name, T expected, T actual) where T : struct
        {
            if (expected.Equals(actual)) Ok(name);
            else Bad(name, Convert.ToString(expected, CultureInfo.InvariantCulture), Convert.ToString(actual, CultureInfo.InvariantCulture));
        }

        /// <summary>把一段可能抛异常的建栈/计算逻辑转成一条断言。</summary>
        public static void Guard(string name, Action body)
        {
            try { body(); Ok(name); }
            catch (Exception e) { Bad(name, "不应抛异常，实际 " + Short(e)); }
        }

        public static void PrintFailures()
        {
            if (Failures.Count == 0) return;
            Console.WriteLine();
            Console.WriteLine("---- 失败 " + Failures.Count + " 条 ----");
            foreach (string f in Failures) Console.WriteLine("  " + f);
        }

        public static string Fmt(double v)
        {
            if (double.IsNaN(v)) return "<NaN>";
            if (double.IsPositiveInfinity(v)) return "<+inf>";
            if (double.IsNegativeInfinity(v)) return "<-inf>";
            return v.ToString("0.########", CultureInfo.InvariantCulture);
        }

        public static string Short(Exception e)
        {
            string t = e.GetType().Name + ": " + (e.Message ?? "");
            if (e is System.ArgumentOutOfRangeException ao) t += " (param=" + ao.ParamName + ")";
            return t.Length > 200 ? t.Substring(0, 200) + "…" : t;
        }

        /// <summary>日志里绝不出现裸控制符；不可见字符渲染成 &lt;U+XXXX&gt;，否则读的人分不清「代码错了」还是「终端显示不了」。</summary>
        public static string Show(string s)
        {
            if (s == null) return "<null>";
            string head = s.Length > 48 ? s.Substring(0, 48) + "…(len=" + s.Length.ToString(CultureInfo.InvariantCulture) + ")" : s;
            var sb = new StringBuilder(head.Length + 8);
            foreach (char c in head)
            {
                if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20 || (c >= 0x7F && c <= 0x9F)) sb.Append("<U+").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture)).Append('>');
                else if (c >= 0xE000 && c <= 0xF8FF) sb.Append("<U+").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture)).Append('>');
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
