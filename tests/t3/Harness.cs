using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ZoneSnapper.T3
{
    /// <summary>
    /// 分组跑断言：每组一个小标题，组内抛出的异常记为该组的一条 FAIL 并继续跑下一组。
    /// 这样「一条断言崩了」不会把后面几百条的信息一起带走 —— 门禁报告必须一次看全。
    /// </summary>
    internal static class Harness
    {
        public static void Section(string title, Action body)
        {
            Check.Group = title;
            Console.WriteLine();
            Console.WriteLine("== " + title + " ==");
            int before = Check.Total;
            var sw = Stopwatch.StartNew();
            try
            {
                body();
            }
            catch (Exception e)
            {
                Check.Bad(title + "：整组未跑完（框架层异常）", "正常跑完 " + Check.Short(e));
            }
            sw.Stop();
            Check.Group = "";
            Console.WriteLine("  -- 本节 " + (Check.Total - before) + " 条，" + sw.ElapsedMilliseconds + " ms");
        }

        /// <summary>浮点比较用的公共容差：几何量级是 1e-2 米，这里取远小于它的判定精度。</summary>
        public const double GEOM_EPS = 1e-9;

        public static bool Near(double a, double b, double tol)
        {
            return !double.IsNaN(a) && !double.IsNaN(b) && Math.Abs(a - b) <= tol;
        }

        public static bool Near(Engine.P3 a, Engine.P3 b, double tol)
        {
            return a.DistanceTo(b) <= tol && Math.Abs(a.H - b.H) <= tol;
        }

        /// <summary>点到折线的最短平面距离（走被测代码自己的算子，不另写一份实现）。</summary>
        public static double DistToLine(Engine.Polyline line, Engine.P3 p)
        {
            return Engine.GeoKit.DistanceFromPoint(line, p);
        }

        public static List<Engine.P3> Pts(IList<Engine.P3> src)
        {
            var l = new List<Engine.P3>(src == null ? 0 : src.Count);
            if (src != null) for (int i = 0; i < src.Count; i++) l.Add(src[i]);
            return l;
        }
    }
}
