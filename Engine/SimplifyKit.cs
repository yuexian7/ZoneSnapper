using System;
using System.Collections.Generic;

namespace ZoneSnapper.Engine
{
    /// <summary>
    /// 节点瘦身。这是需求 9 的真正落点。
    ///
    /// 事实前提（FACT：ilspycmd -t Game.Areas.Node → 只有 float3 m_Position + float m_Elevation）：
    /// 游戏的区域边界是**折线**，数据结构里没有曲线段，三角化与边界渲染都按折线走。
    /// 所以「两个节点之间弯着连」在数据层不可能成真曲线；能做的是
    /// 「用尽可能少的折点把弯描出来」。原模组 Subdivisions 的做法是密集放点
    /// （商店页与更新日志里只有一个笼统的 Simplification 滑杆），本模组用自适应容差把它压到几个点：
    ///   容差 = f(该类别的贴合半径, 简化滑杆)，见 PolicyKit.Resolve。
    /// </summary>
    public static class SimplifyKit
    {
        /// <summary>
        /// Ramer–Douglas–Peucker：在容差内砍掉尽可能多的点，保留弯道形状。
        /// 显式栈实现（不递归）：一条描边可能有上千个采样点，递归深度不可控。
        /// </summary>
        public static List<P3> Simplify(IList<P3> pts, double tolerance)
        {
            return Simplify(pts, tolerance, (List<P3>)null);
        }

        /// <summary>
        /// 同上，多一个 <paramref name="pins"/>：这些**位置**（按坐标比对）必须留在结果里。
        ///
        /// 需求 2 的硬要求：「道路类型发生变化的那个交叉点必须放一个节点」。
        /// 纯 RDP 做不到 —— 路变铁路但方向几乎不变时，那个交叉点在容差内就是可删的，
        /// 删掉之后边界看上去直接穿过了道岔，玩家说的「贴合的节点不对」就是这个。
        ///
        /// 实现按钉把输入**切成若干区间分别跑 RDP**，钉先进 keep[]：区间端点在 RDP 里永不被删
        /// （<c>keep[first]</c>/<c>keep[last]</c> 写死），所以不用动主循环判据，也就不存在
        /// 「结果索引与原索引对不上」的那类错。用坐标而不是下标比对，是因为同一个点还要在
        /// <see cref="MergeCollinear"/> 与 <see cref="Dedupe"/> 里各认一次，而下标每过一道闸就变。
        /// </summary>
        public static List<P3> Simplify(IList<P3> pts, double tolerance, List<P3> pins)
        {
            List<P3> outPts = new List<P3>();
            if (pts == null || pts.Count == 0) return outPts;
            if (pts.Count < 3 || tolerance <= 0)
            {
                outPts.AddRange(pts);
                return outPts;
            }

            bool[] keep = new bool[pts.Count];
            keep[0] = true;
            keep[pts.Count - 1] = true;

            Stack<int[]> stack = new Stack<int[]>();
            if (pins == null || pins.Count == 0)
            {
                stack.Push(new int[] { 0, pts.Count - 1 });
            }
            else
            {
                // 钉点先进 keep[]，再以钉点为界把输入切成若干区间分别跑 RDP。
                int prev = 0;
                for (int i = 1; i < pts.Count; i++)
                {
                    if (!IsPinned(pts[i], pins)) continue;
                    keep[i] = true;
                    if (i - prev >= 2) stack.Push(new int[] { prev, i });
                    prev = i;
                }
                if (pts.Count - 1 - prev >= 2) stack.Push(new int[] { prev, pts.Count - 1 });
            }

            while (stack.Count > 0)
            {
                int[] span = stack.Pop();
                int i = span[0];
                int j = span[1];
                if (j <= i + 1) continue;
                double maxD = -1;
                int maxIdx = -1;
                for (int k = i + 1; k < j; k++)
                {
                    double t;
                    double d;
                    GeoKit.ClosestPointOnSegment(pts[i], pts[j], pts[k], out t, out d);
                    if (d > maxD) { maxD = d; maxIdx = k; }
                }
                if (maxD > tolerance && maxIdx > i)
                {
                    keep[maxIdx] = true;
                    stack.Push(new int[] { i, maxIdx });
                    stack.Push(new int[] { maxIdx, j });
                }
            }
            for (int c = 0; c < pts.Count; c++) if (keep[c]) outPts.Add(pts[c]);
            return outPts;
        }

        /// <summary>
        /// 丢掉过近的点（保留方向突变的那个）。
        /// 必要性不是美学：游戏在提交时会检查「最后两格距离 &gt;= m_SnapDistance*0.5」
        /// （FACT：ATS:3509-3511 + AreaUtils.GetMinNodeDistance），而重合点会让
        /// Areas 的三角化产生退化三角形 —— 表现就是区域画好了却提交不了（AreaFlags.Complete 不成立）。
        /// </summary>
        public static List<P3> Dedupe(IList<P3> pts, double minSpacing)
        {
            return Dedupe(pts, minSpacing, null);
        }

        public static List<P3> Dedupe(IList<P3> pts, double minSpacing, List<P3> pins)
        {
            List<P3> outPts = new List<P3>();
            if (pts == null || pts.Count == 0) return outPts;
            outPts.Add(pts[0]);
            for (int i = 1; i < pts.Count; i++)
            {
                P3 cand = pts[i];
                P3 last = outPts[outPts.Count - 1];
                if (cand.DistanceTo(last) >= minSpacing || IsPinned(cand, pins))
                {
                    outPts.Add(cand);
                }
                else if (i == pts.Count - 1)
                {
                    // 末点必须站住：它是这条边接向下一个手动节点的出口。
                    // 做法是**替换**上一个保留点（而不是追加）—— 追加会出现间距小于 minSpacing 的一对，
                    // 替换则退化成「整链太密 ⇒ 只剩末点一个点」，两条判据同时成立。
                    // 但被替换掉的那个点如果是钉（类型变化处），这一换就把需求 2 的节点换没了 ⇒ 只在非钉时替换。
                    if (!IsPinned(outPts[outPts.Count - 1], pins)) outPts[outPts.Count - 1] = cand;
                    else outPts.Add(cand);
                }
            }
            return outPts;
        }

        /// <summary>
        /// 「这个点是不是钉住的」—— 按坐标比，容差 1 微米。
        /// 钉的个数是一条描边用过的边数（走廊内几十个），线性扫比建哈希集更省，也不会有哈希分家问题
        /// （海岸线缝合那段踩过：同一个点两次算出来差 1 个 ULP）。
        /// </summary>
        private static bool IsPinned(P3 p, List<P3> pins)
        {
            if (pins == null || pins.Count == 0) return false;
            for (int i = 0; i < pins.Count; i++) if (pins[i].NearlyEquals(p, 1e-6)) return true;
            return false;
        }

        /// <summary>合并「几乎笔直」的相邻段（道路直段上采样出来的共线点，一个都不该留）。</summary>
        public static List<P3> MergeCollinear(IList<P3> pts, double straightCos)
        {
            return MergeCollinear(pts, straightCos, null);
        }

        /// <summary>
        /// 同上，但 <paramref name="pins"/> 里的位置（按坐标比对，最多几十个）绝不合并掉。
        /// 需求 2 的「类型变化处必须有节点」要被三道闸同时尊重：RDP 会删它、这里也会把它当成
        /// 「前后同方向的拐点」抹平、<see cref="Dedupe"/> 还会因为离邻点太近丢掉它。
        /// 只补一道等于没补。
        /// </summary>
        public static List<P3> MergeCollinear(IList<P3> pts, double straightCos, List<P3> pins)
        {
            List<P3> outPts = new List<P3>();
            if (pts == null || pts.Count < 3) { if (pts != null) outPts.AddRange(pts); return outPts; }
            outPts.Add(pts[0]);
            for (int i = 1; i < pts.Count - 1; i++)
            {
                V2 a = V2.From(outPts[outPts.Count - 1]);
                V2 b = V2.From(pts[i]);
                V2 c = V2.From(pts[i + 1]);
                V2 ab = (b - a).Normalized();
                V2 bc = (c - b).Normalized();
                double dot = ab.Dot(bc);
                // dot≈1 表示前后方向一致（该点可删）；dot 越小弯得越厉害（该点必须留）。
                if (dot <= straightCos || IsPinned(pts[i], pins)) outPts.Add(pts[i]);
            }
            outPts.Add(pts[pts.Count - 1]);
            return outPts;
        }

        /// <summary>
        /// 折线（**开链**）自交检测，跳过相邻段。
        /// 旧实现里有一句 `if (i == 0 && j + 1 == Count - 1) continue`，那是闭合环才该跳的
        /// （环的首尾两段本来就共享端点），而调用方传的是 [起点]+中间点+[终点] 这种开链 ——
        /// 于是四点的最小蝴蝶结恰好落在首尾两段上，被静默放过（回归壳 S5 抓到）。
        /// 闭合环请用 <see cref="RingSelfIntersects"/>。
        /// </summary>
        public static bool SelfIntersects(IList<P3> pts)
        {
            if (pts == null || pts.Count < 4) return false;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                for (int j = i + 2; j + 1 < pts.Count; j++)
                {
                    if (GeoKit.SegmentsIntersect(pts[i], pts[i + 1], pts[j], pts[j + 1])) return true;
                }
            }
            return false;
        }

        /// <summary>整条环（首尾不重复存储的闭合多边形）自交检测。</summary>
        public static bool RingSelfIntersects(IList<P3> ring)
        {
            if (ring == null || ring.Count < 4) return false;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                P3 a0 = ring[i];
                P3 a1 = ring[(i + 1) % n];
                for (int j = i + 1; j < n; j++)
                {
                    // 跳过共享端点的相邻边
                    if (j == i || (j + 1) % n == i || j == (i + 1) % n) continue;
                    P3 b0 = ring[j];
                    P3 b1 = ring[(j + 1) % n];
                    if (GeoKit.SegmentsIntersect(a0, a1, b0, b1)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 节点预算：先按容差简化，还是超预算就按比例放大容差重试（最多 6 次）。
        /// 这条循环是「大区域画一圈产生上千节点」的保险 —— 节点数直接决定游戏侧
        /// 三角化耗时与存档体积，宁可形状糙一点也不能失控。
        /// </summary>
        public static List<P3> FitToBudget(IList<P3> pts, double tolerance, double minSpacing, int maxNodes, out double usedTolerance)
        {
            return FitToBudget(pts, tolerance, 0.0, minSpacing, maxNodes, null, out usedTolerance);
        }

        /// <summary>
        /// 同上，多两道闸：<paramref name="hardTolerance"/> 是**简化容差的上限**，
        /// <paramref name="pins"/> 是**任何一道闸都不许删**的位置（需求 2 的类型变化处）。
        /// </summary>
        public static List<P3> FitToBudget(IList<P3> pts, double tolerance, double hardTolerance, double minSpacing, int maxNodes, List<P3> pins, out double usedTolerance)
        {
            double tol = tolerance;
            if (hardTolerance > 0 && tol > hardTolerance) tol = hardTolerance;
            usedTolerance = tol;
            List<P3> cur = Passes(pts, tol, minSpacing, pins);
            int guard = 0;
            double t = tol;
            while (cur.Count > maxNodes && guard++ < 6)
            {
                double next = Math.Max(t * 1.8, minSpacing * 2.0);
                if (hardTolerance > 0 && next > hardTolerance)
                {
                    // 已经顶到硬界还超预算：接受这个点数，不再放粗。
                    break;
                }
                t = next;
                cur = Passes(pts, t, minSpacing, pins);
            }
            usedTolerance = t;
            return cur;
        }

        /// <summary>一条候选折线过三道简化闸的固定顺序：RDP → 合共线 → 去近邻。三道都必须认钉。</summary>
        private static List<P3> Passes(IList<P3> pts, double tolerance, double minSpacing, List<P3> pins)
        {
            List<P3> cur = Simplify(pts, tolerance, pins);
            cur = MergeCollinear(cur, STRAIGHT_COS, pins);
            cur = Dedupe(cur, minSpacing, pins);
            return cur;
        }

        /// <summary>cos(2°)：前后转向角小于 2° 视为「直」，中间点可删；弯一点的绝不删。</summary>
        public const double STRAIGHT_COS = 0.99939;

        /// <summary>
        /// 退化区域判定：面积小到这个量级以下就不要提交了。
        /// 对应原模组 0.2.3 修的「districts collapsing into a thin sliver」——
        /// 两点落在同一条环路的不同侧时，直连会退化，本模组的对策是绕行 + 这道兜底闸。
        /// </summary>
        public static bool IsDegenerateRing(IList<P3> ring, double minArea)
        {
            return Math.Abs(GeoKit.SignedArea2D(ring)) < minArea;
        }
    }
}
