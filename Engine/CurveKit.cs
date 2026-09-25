using System;
using System.Collections.Generic;

namespace ZoneSnapper.Engine
{
    /// <summary>
    /// 两点之间的「弯曲连接」。需求 9 的替代实现。
    ///
    /// 为什么不是真曲线（FACT：Game.Areas.Node 只有 float3 m_Position + float m_Elevation，
    /// 三角化与边界渲染都按折线处理）：数据结构里没有曲线段，写不进去。
    /// 所以这里做的是「用尽可能少的折点把弯撑出来」——玩家看到的仍是弯的，
    /// 而节点数由 segments 严格控制（默认 3，即一条弯只用 3 个中间点，
    /// 对比原模组密集放点的做法是数量级的差别）。
    /// </summary>
    public static class CurveKit
    {
        /// <summary>
        /// 圆弧：a→b，中点沿法向偏移 bulge × |ab|。segments 为中间点数（不含两端）。
        /// segments &lt;= 0 时返回空（等于不弯曲）。
        /// </summary>
        public static List<P3> Arc(P3 a, P3 b, double bulge, int segments)
        {
            List<P3> outPts = new List<P3>();
            if (segments <= 0) return outPts;
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len <= 1e-9) return outPts;
            // 法向：把 (dx,dy) 逆时针转 90°
            double nx = -dy / len;
            double ny = dx / len;
            P3 mid = new P3(
                (a.X + b.X) * 0.5 + nx * bulge * len,
                (a.Y + b.Y) * 0.5 + ny * bulge * len,
                (a.H + b.H) * 0.5);
            // 二次贝塞尔（起点 a、控制点 = mid 的两倍外推、终点 b）：
            // 让曲线真正「经过」mid，而不是被控制点拽得更鼓。
            P3 ctrl = new P3(2 * mid.X - (a.X + b.X) * 0.5, 2 * mid.Y - (a.Y + b.Y) * 0.5, 2 * mid.H - (a.H + b.H) * 0.5);
            for (int i = 1; i <= segments; i++)
            {
                double t = (double)i / (segments + 1);
                outPts.Add(QuadBezier(a, ctrl, b, t));
            }
            return outPts;
        }

        private static P3 QuadBezier(P3 p0, P3 p1, P3 p2, double t)
        {
            double u = 1 - t;
            double a = u * u;
            double b = 2 * u * t;
            double c = t * t;
            return new P3(
                a * p0.X + b * p1.X + c * p2.X,
                a * p0.Y + b * p1.Y + c * p2.Y,
                a * p0.H + b * p1.H + c * p2.H);
        }

        /// <summary>
        /// Catmull-Rom 样条过一串点（用于「整圈边界平滑」而非单边弯曲）。
        /// 输出每个 span 内的 segments 个中间点，不含原始点。
        /// </summary>
        public static List<P3> Smooth(IList<P3> ring, bool closed, int segments)
        {
            List<P3> outPts = new List<P3>();
            if (ring == null || ring.Count < 3 || segments <= 0) return outPts;
            int n = ring.Count;
            int spans = closed ? n : n - 1;
            for (int i = 0; i < spans; i++)
            {
                P3 p0 = ring[Idx(ring, closed, i - 1)];
                P3 p1 = ring[Idx(ring, closed, i)];
                P3 p2 = ring[Idx(ring, closed, i + 1)];
                P3 p3 = ring[Idx(ring, closed, i + 2)];
                for (int s = 1; s <= segments; s++)
                {
                    double t = (double)s / (segments + 1);
                    outPts.Add(CatmullRom(p0, p1, p2, p3, t));
                }
            }
            return outPts;
        }

        private static int Idx(IList<P3> list, bool closed, int i)
        {
            int n = list.Count;
            if (closed) { int m = i % n; return m < 0 ? m + n : m; }
            if (i < 0) return 0;
            if (i > n - 1) return n - 1;
            return i;
        }

        private static P3 CatmullRom(P3 p0, P3 p1, P3 p2, P3 p3, double t)
        {
            double t2 = t * t;
            double t3 = t2 * t;
            return new P3(
                0.5 * ((2 * p1.X) + (-p0.X + p2.X) * t + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3),
                0.5 * ((2 * p1.Y) + (-p0.Y + p2.Y) * t + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3),
                0.5 * ((2 * p1.H) + (-p0.H + p2.H) * t + (2 * p0.H - 5 * p1.H + 4 * p2.H - p3.H) * t2 + (-p0.H + 3 * p1.H - 3 * p2.H + p3.H) * t3));
        }
    }
}
