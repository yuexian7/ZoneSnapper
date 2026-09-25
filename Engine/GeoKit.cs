using System;
using System.Collections.Generic;

namespace ZoneSnapper.Engine
{
    /// <summary>
    /// 引擎层的空间点：X=世界 X，Y=世界 Z，H=世界高度。
    ///
    /// 为什么单独造一个类型而不用 Unity.Mathematics.float3 / Colossal.Mathematics：
    /// 开发标准流程 Playbook 硬规则 14 —— 「想离线测某个方法，它所在的类必须一个游戏类型都不碰」，
    /// 否则离线回归壳（tests/t3，零游戏 DLL 引用）加载类型时会 TypeLoadException。
    /// 平面几何一律用 double（城市坐标可达 ±8e4，float 在这个量级只剩约 1e-3 分辨率，
    /// 而共线/自交判定要在 1e-2 量级上做决策），H 只在最终写回控制点时才用得上。
    /// </summary>
    public struct P3
    {
        public double X;
        public double Y;
        public double H;

        public P3(double x, double y, double h)
        {
            X = x;
            Y = y;
            H = h;
        }

        /// <summary>平面距离（忽略高度）。区域边界是 XZ 平面的多边形，所有几何判定都只看平面。</summary>
        public double DistanceTo(P3 other)
        {
            double dx = X - other.X;
            double dy = Y - other.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public double DistanceSquaredTo(P3 other)
        {
            double dx = X - other.X;
            double dy = Y - other.Y;
            return dx * dx + dy * dy;
        }

        /// <summary>线性插值。高度一起插值，保证描边点落在道路曲线的实际高程上而不是地形面上。</summary>
        public static P3 Lerp(P3 a, P3 b, double t)
        {
            return new P3(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.H + (b.H - a.H) * t);
        }

        public bool NearlyEquals(P3 other, double eps)
        {
            double dx = X - other.X;
            double dy = Y - other.Y;
            return (dx * dx + dy * dy) <= eps * eps;
        }

        public override string ToString()
        {
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "({0:F2}, {1:F2} @ {2:F2})", X, Y, H);
        }
    }

    /// <summary>二维向量（XZ 平面），只服务于本引擎内部的几何判定。</summary>
    public struct V2
    {
        public double X;
        public double Y;

        public V2(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double Length { get { return Math.Sqrt(X * X + Y * Y); } }

        public static V2 operator +(V2 a, V2 b) { return new V2(a.X + b.X, a.Y + b.Y); }
        public static V2 operator -(V2 a, V2 b) { return new V2(a.X - b.X, a.Y - b.Y); }
        public static V2 operator *(V2 a, double k) { return new V2(a.X * k, a.Y * k); }

        public double Dot(V2 b) { return X * b.X + Y * b.Y; }

        /// <summary>二维叉积的 z 分量：&gt;0 表示 b 在 a 的左手侧（XZ 平面按 X→Z 顺时针看的约定见 CrossZ 用法注释）。</summary>
        public double CrossZ(V2 b) { return X * b.Y - Y * b.X; }

        public static V2 From(P3 p) { return new V2(p.X, p.Y); }
        public V2 Normalized()
        {
            double len = Length;
            if (len <= 1e-12) return new V2(0, 0);
            return new V2(X / len, Y / len);
        }
    }

    /// <summary>一条折线（描边结果、区域边界、网络曲线的采样都用它）。</summary>
    public sealed class Polyline
    {
        public readonly List<P3> Points;

        public Polyline()
        {
            Points = new List<P3>();
        }

        public Polyline(List<P3> points)
        {
            Points = points ?? new List<P3>();
        }

        public int Count { get { return Points.Count; } }

        public bool IsEmpty { get { return Points.Count < 2; } }

        /// <summary>平面总长。</summary>
        public double Length2D()
        {
            double sum = 0;
            for (int i = 1; i < Points.Count; i++) sum += Points[i - 1].DistanceTo(Points[i]);
            return sum;
        }

        /// <summary>包围盒（XZ）。用于「脏矩形是否碰到这条走廊」的相交判断。</summary>
        public Box2 Bounds()
        {
            Box2 b = Box2.Empty();
            for (int i = 0; i < Points.Count; i++) b.Expand(Points[i]);
            return b;
        }

        public void Add(P3 p) { Points.Add(p); }
        public void AddRange(IEnumerable<P3> ps) { Points.AddRange(ps); }
    }

    /// <summary>XZ 平面轴对齐框。</summary>
    public struct Box2
    {
        public double MinX;
        public double MinY;
        public double MaxX;
        public double MaxY;

        public static Box2 Empty()
        {
            return new Box2 { MinX = double.PositiveInfinity, MinY = double.PositiveInfinity, MaxX = double.NegativeInfinity, MaxY = double.NegativeInfinity };
        }

        public bool IsValid { get { return MinX <= MaxX && MinY <= MaxY; } }

        public void Expand(P3 p)
        {
            if (p.X < MinX) MinX = p.X;
            if (p.Y < MinY) MinY = p.Y;
            if (p.X > MaxX) MaxX = p.X;
            if (p.Y > MaxY) MaxY = p.Y;
        }

        /// <summary>并集（空集与任何集求并 = 那个集本身）。单调扩张用这个，不自己写四行 min/max。</summary>
        public Box2 Union(Box2 o)
        {
            if (!o.IsValid) return this;
            if (!IsValid) return o;
            return new Box2
            {
                MinX = Math.Min(MinX, o.MinX),
                MinY = Math.Min(MinY, o.MinY),
                MaxX = Math.Max(MaxX, o.MaxX),
                MaxY = Math.Max(MaxY, o.MaxY)
            };
        }

        /// <summary>最长那条边的长度（无效框返回 0），用来给走廊设尺寸上限。</summary>
        public double MaxSide
        {
            get
            {
                if (!IsValid) return 0;
                return Math.Max(MaxX - MinX, MaxY - MinY);
            }
        }

        public Box2 Inflated(double r)
        {
            return new Box2 { MinX = MinX - r, MinY = MinY - r, MaxX = MaxX + r, MaxY = MaxY + r };
        }

        public bool Intersects(Box2 o)
        {
            if (!IsValid || !o.IsValid) return false;
            return !(o.MinX > MaxX || o.MaxX < MinX || o.MinY > MaxY || o.MaxY < MinY);
        }

        public bool Contains(P3 p)
        {
            return p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;
        }

        /// <summary>到框的最短平面距离（框内为 0）。</summary>
        public double DistanceTo(P3 p)
        {
            double dx = 0;
            if (p.X < MinX) dx = MinX - p.X; else if (p.X > MaxX) dx = p.X - MaxX;
            double dy = 0;
            if (p.Y < MinY) dy = MinY - p.Y; else if (p.Y > MaxY) dy = p.Y - MaxY;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    /// <summary>
    /// 基础几何算子。刻意做成静态无状态方法：T3 回归壳直接对它们断言，
    /// 不需要任何游戏侧对象。
    /// </summary>
    public static class GeoKit
    {
        public const double EPS = 1e-9;

        /// <summary>
        /// 点到线段的最近点。t 返回参数（0..1 夹取），d 返回距离。
        /// 这是贴合的最小原语：游戏自己的实现是 Colossal.Mathematics.MathUtils.Distance(Bezier4x3, float3, out t)
        /// （反编译证据：MathUtils dump :772），我们把贝塞尔在游戏侧采样成折线段后用它逐段求，
        /// 这样引擎层不必依赖贝塞尔类型，简化/自交判定也共用同一套折线代码。
        /// </summary>
        public static P3 ClosestPointOnSegment(P3 a, P3 b, P3 p, out double t, out double d)
        {
            V2 ab = V2.From(b) - V2.From(a);
            double len2 = ab.Dot(ab);
            if (len2 <= EPS)
            {
                t = 0;
                d = p.DistanceTo(a);
                return a;
            }
            double raw = (V2.From(p) - V2.From(a)).Dot(ab) / len2;
            t = Clamp(raw, 0, 1);
            P3 hit = P3.Lerp(a, b, t);
            d = p.DistanceTo(hit);
            return hit;
        }

        /// <summary>折线上最近点：返回点、所在段序号、段内参数、总长进度（弧长参数，便于环形取子段）。</summary>
        public static void ClosestPointOnPolyline(Polyline line, P3 p, out P3 best, out int segIndex, out double segT, out double arcLength, out double dist)
        {
            best = p;
            segIndex = -1;
            segT = 0;
            arcLength = 0;
            dist = double.PositiveInfinity;
            if (line == null || line.Count == 0) return;
            if (line.Count == 1)
            {
                best = line.Points[0];
                dist = p.DistanceTo(best);
                return;
            }
            double acc = 0;
            for (int i = 0; i + 1 < line.Count; i++)
            {
                P3 a = line.Points[i];
                P3 b = line.Points[i + 1];
                double t;
                double d;
                P3 hit = ClosestPointOnSegment(a, b, p, out t, out d);
                if (d < dist)
                {
                    dist = d;
                    best = hit;
                    segIndex = i;
                    segT = t;
                    arcLength = acc + a.DistanceTo(hit);
                }
                acc += a.DistanceTo(b);
            }
        }

        /// <summary>按弧长参数取点（用于「同一条曲线两点之间取子段」）。arc 会夹到 [0, total]。</summary>
        public static P3 PointAtArcLength(Polyline line, double arc, out double clampedArc)
        {
            clampedArc = 0;
            if (line == null || line.Count == 0) return default(P3);
            if (line.Count == 1) return line.Points[0];
            double total = line.Length2D();
            double want = Clamp(arc, 0, total);
            clampedArc = want;
            double acc = 0;
            for (int i = 0; i + 1 < line.Count; i++)
            {
                double seg = line.Points[i].DistanceTo(line.Points[i + 1]);
                if (acc + seg >= want || i + 2 == line.Count)
                {
                    double t = seg <= EPS ? 0 : Clamp((want - acc) / seg, 0, 1);
                    return P3.Lerp(line.Points[i], line.Points[i + 1], t);
                }
                acc += seg;
            }
            return line.Points[line.Count - 1];
        }

        /// <summary>
        /// 折线子段：[arcA, arcB] 之间的部分（含两端精确插值点）。
        /// 约定 b &lt; a 时返回空 —— 「跨过端点怎么绕」是策略，归 TraceKit 的绕行分支决定，
        /// 这里只提供确定的几何切片。（旧注释写「按跨端点处理」与代码不符，由回归壳指出后改正。）
        /// 只加**严格落在 (a, b) 内**的内部顶点，所以结果绝不会越过 arcB 再折回来。
        /// </summary>
        public static Polyline Subspan(Polyline line, double arcA, double arcB)
        {
            Polyline outLine = new Polyline();
            if (line == null || line.Count < 2) return outLine;
            double total = line.Length2D();
            double a = Clamp(arcA, 0, total);
            double b = Clamp(arcB, 0, total);
            if (b < a) return outLine;

            double dummy;
            outLine.Add(PointAtArcLength(line, a, out dummy));
            double arc = 0;
            for (int k = 1; k < line.Count - 1; k++)
            {
                arc += line.Points[k - 1].DistanceTo(line.Points[k]);
                if (arc > a + EPS && arc < b - EPS) PushUnique(outLine, line.Points[k]);
            }
            PushUnique(outLine, PointAtArcLength(line, b, out dummy));
            return outLine;
        }

        private static void PushUnique(Polyline into, P3 p)
        {
            if (into.Count == 0) { into.Add(p); return; }
            if (into.Points[into.Count - 1].DistanceSquaredTo(p) > EPS) into.Add(p);
        }

        /// <summary>叉积符号。XZ 平面：CrossZ(b-a, c-a) &gt; 0 表示 a→b→c 左转。</summary>
        public static double CrossZ(P3 a, P3 b, P3 c)
        {
            return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        }

        /// <summary>
        /// 线段严格相交（共线/端点相触不算）。自交检测是「描边把区域打成蝴蝶结」的保险丝，
        /// 原模组 0.2.3 修的就是这个 bug，我们没有它的代码，只能自己判定后回退直线。
        /// </summary>
        public static bool SegmentsIntersect(P3 p1, P3 p2, P3 p3, P3 p4)
        {
            double d1 = CrossZ(p3, p4, p1);
            double d2 = CrossZ(p3, p4, p2);
            double d3 = CrossZ(p1, p2, p3);
            double d4 = CrossZ(p1, p2, p4);
            if (((d1 > EPS && d2 < -EPS) || (d1 < -EPS && d2 > EPS)) &&
                ((d3 > EPS && d4 < -EPS) || (d3 < -EPS && d4 > EPS))) return true;
            return false;
        }

        /// <summary>多边形有向面积（XZ）。退化检查与「绕行方向选哪一侧」都靠它的符号与量级。</summary>
        public static double SignedArea2D(IList<P3> ring)
        {
            if (ring == null || ring.Count < 3) return 0;
            double sum = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                P3 a = ring[i];
                P3 b = ring[(i + 1) % ring.Count];
                sum += a.X * b.Y - b.X * a.Y;
            }
            return sum * 0.5;
        }

        /// <summary>
        /// NaN 一律落到下限：几何里冒出的 NaN（0 长度曲线、坏数据）如果被原样传下去，
        /// 会污染 SnapRadius / SimplifyTolerance，后果是「模组静默失效」而不是报错 —— 最难查的那种。
        /// </summary>
        public static double Clamp(double v, double lo, double hi)
        {
            if (double.IsNaN(v) || v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        /// <summary>
        /// 脏输入消毒：NaN / ±∞ 一律当 0（= 「这一项没读到」），有限值原样返回。
        /// <see cref="Clamp"/> 只在有上下界的地方顺手消毒，而有些量（路宽、中线偏移、高程）
        /// 读出来直接进算术，没有上下界可夹，就需要这一步。
        /// </summary>
        public static double FiniteOrZero(double v)
        {
            return double.IsNaN(v) || double.IsInfinity(v) ? 0.0 : v;
        }

        /// <summary>折线到点的最近平面距离（用于「这条边是否还在脏矩形附近」的粗筛）。</summary>
        public static double DistanceFromPoint(Polyline line, P3 p)
        {
            P3 best;
            int si;
            double st, al, d;
            ClosestPointOnPolyline(line, p, out best, out si, out st, out al, out d);
            return d;
        }

        /// <summary>
        /// 折线最近点的「返回值即距离」版本，贴合打分里到处要用，比 5 个 out 参数顺手。
        /// </summary>
        public static double ClosestPointOnPolylineProbe(Polyline line, P3 p, out P3 best, out int segIndex, out double segT, out double arcLength)
        {
            P3 b;
            int si;
            double st, al, d;
            ClosestPointOnPolyline(line, p, out b, out si, out st, out al, out d);
            best = b;
            segIndex = si;
            segT = st;
            arcLength = al;
            return d;
        }

        /// <summary>
        /// 折线第 <paramref name="i"/> 个顶点处的局部曲率半径（三点外接圆，米）。
        /// 端点取相邻内侧点的值；三点共线或退化返回 <see cref="double.PositiveInfinity"/>（=直路，随便放点）。
        ///
        /// 需求 2 的弯道节点密度要用它：弧长 s 对应的**弦高**（弦与弧之间的最大偏离）是
        /// <c>h ≈ s²/(8R)</c>，反过来「弦高不超过 h₀」给出最大弦长 <c>s ≤ √(8·R·h₀)</c>。
        /// 这就是「任意相邻两点的连线不要超出道路边缘」的可计算形式。
        /// </summary>
        public static double LocalRadius(Polyline line, int i)
        {
            if (line == null || line.Count < 3) return double.PositiveInfinity;
            int k = ClampIndex(i, line.Count);
            int ia = k - 1 < 0 ? k + 1 : k - 1;
            int ib = k + 1 >= line.Count ? k - 1 : k + 1;
            if (ia < 0 || ib < 0 || ia >= line.Count || ib >= line.Count) return double.PositiveInfinity;
            return CircumRadius(line.Points[ia], line.Points[k], line.Points[ib]);
        }

        /// <summary>三点外接圆半径；共线/重合返回 +∞。</summary>
        public static double CircumRadius(P3 a, P3 b, P3 c)
        {
            double la = b.DistanceTo(c);     // 对边（不含 a）
            double lb = a.DistanceTo(c);
            double lc = a.DistanceTo(b);
            if (!(la > EPS) || !(lb > EPS) || !(lc > EPS)) return double.PositiveInfinity;
            // 面积用叉积的一半；叉积趋 0 即共线，此时半径趋无穷 —— 直接判下限，避免 2*0 除出天文数字。
            double cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            double area2 = Math.Abs(cross);
            if (area2 <= 1e-9) return double.PositiveInfinity;
            return (la * lb * lc) / (2.0 * area2);
        }

        /// <summary>
        /// 折线 [arcA, arcB] 段上，一条从 arcA 拉到 arcB 的弦最多偏离曲线多远（米）。
        /// 也就是「两点一线连起来会切掉多大一块」，是需求 2/3 里判定弦能不能交给游戏的那道闸。
        /// </summary>
        public static double ChordDeviation(Polyline line, double arcA, double arcB)
        {
            Polyline span = Subspan(line, arcA, arcB);
            if (span.Count < 3) return 0;
            P3 a = span.Points[0];
            P3 b = span.Points[span.Count - 1];
            double worst = 0;
            for (int i = 1; i + 1 < span.Count; i++)
            {
                double t, d;
                ClosestPointOnSegment(a, b, span.Points[i], out t, out d);
                if (d > worst) worst = d;
            }
            return worst;
        }

        /// <summary>
        /// 按「相邻两点连线的弦高 ≤ <paramref name="maxDeviation"/>」重采样子段，返回**不含两端**的中间点。
        ///
        /// 与 <see cref="SimplifyKit"/> 的 Douglas–Peucker 是同一件事的两个方向：RDP 是「能省则省」，
        /// 这里是「不能省到超出容差」，所以必须从左往右贪心推进 —— 递归二分会在长直段里放很少的点、
        /// 在弯段里点数不可控，而玩家要的正是「直路两个点、弯道密到看不出空隙」。
        ///
        /// 每一步先按**局部曲率半径**给出解析预算 <c>s ≤ √(8·R·tol)</c>（需求 2 的原话），
        /// 再用实测弦高兜底收缩：一条边里可能连着两个反向弯（S 形），只看起点那一处的曲率会算长。
        ///
        /// <paramref name="minStep"/> 是相邻节点的最小间距。游戏侧
        /// <c>AreaUtils.GetMinNodeDistance</c> 会**静默吃掉**过近的相邻节点（Lot 8 米 / District 32 米），
        /// 比它更密的点等于白放，所以这里绝不推得更近；连最小间距都超容差时（急弯 + 大最小间距）
        /// 就按最小间距尽力而为，剩下的空隙由简化容差与实机观感决定。
        /// </summary>
        public static List<P3> ChordSafePoints(Polyline line, double arcA, double arcB, double maxDeviation, double minStep, int maxNodes)
        {
            List<P3> mid = new List<P3>();
            Polyline span = Subspan(line, arcA, arcB);
            if (span == null || span.Count < 3) return mid;
            if (!(maxDeviation > 0)) return mid;          // 容差 0/NaN ⇒ 不干预，交给上游简化
            if (maxNodes < 1) maxNodes = 1;
            if (!(minStep > 0)) minStep = 0.5;
            // 游戏侧把贝塞尔按固定步长采样成折线后我们才拿到它，折线弦高**系统性低估**真曲线的弦高
            // （低估量 ≈ 采样步长²/(8R)）。按 0.7 收，留出的余量刚好覆盖 2 米采样在 R≥5 米弯道上的误差。
            double tol = maxDeviation * 0.7;

            int n = span.Count;
            double[] arc = new double[n];
            double[] rad = new double[n];                 // 每个顶点处的局部曲率半径
            arc[0] = 0;
            for (int i = 1; i < n; i++) arc[i] = arc[i - 1] + span.Points[i - 1].DistanceTo(span.Points[i]);
            for (int i = 0; i < n; i++) rad[i] = LocalRadius(span, i);
            double total = arc[n - 1];

            double cur = 0;
            int guard = 0;
            while (cur < total - EPS && mid.Count < maxNodes && guard++ <= maxNodes + 4)
            {
                double want = total;
                for (int k = 0; k < 10; k++)
                {
                    double rMin = MinRadiusBetween(rad, arc, cur, want);
                    double budget = double.IsPositiveInfinity(rMin)
                        ? total - cur                                   // 直路：一步到终点
                        : Math.Sqrt(8.0 * rMin * tol);
                    double allowed = Math.Min(total - cur, Math.Max(budget, minStep));
                    if (allowed >= want - cur - 1e-9)
                    {
                        if (ChordDeviationBetween(span, arc, cur, want) <= maxDeviation) break;
                        allowed = (want - cur) * 0.6;                   // 实测不合格 ⇒ 硬收缩
                    }
                    want = cur + allowed;
                    if (want - cur <= minStep * 1.001) { want = cur + minStep; break; }
                }
                if (want >= total - EPS) break;         // 最后一根弦已经合格 ⇒ 由 b 端直接收口
                double dummy;
                mid.Add(PointAtArcLength(span, want, out dummy));
                cur = want;
            }
            return mid;
        }

        /// <summary>弧长区间 (arcA, arcB) 内各顶点局部曲率半径的最小值；区间内没有顶点就取端点处的。</summary>
        public static double MinRadiusBetween(double[] rad, double[] arc, double arcA, double arcB)
        {
            if (rad == null || arc == null || rad.Length != arc.Length || rad.Length == 0) return double.PositiveInfinity;
            double best = double.PositiveInfinity;
            bool any = false;
            for (int i = 0; i < arc.Length; i++)
            {
                if (arc[i] <= arcA + EPS || arc[i] >= arcB - EPS) continue;
                any = true;
                if (rad[i] < best) best = rad[i];
            }
            if (!any)
            {
                // 区间落在两个采样顶点之间：这两点之间的曲线近似直，取外侧更保守的那个。
                int i0 = LastIndexAtOrBefore(arc, arcA);
                int i1 = FirstIndexAtOrAfter(arc, arcB);
                double a = (i0 >= 0 && i0 < rad.Length) ? rad[i0] : double.PositiveInfinity;
                double b = (i1 >= 0 && i1 < rad.Length) ? rad[i1] : double.PositiveInfinity;
                best = a < b ? a : b;
            }
            return best;
        }

        private static int LastIndexAtOrBefore(double[] arc, double v)
        {
            int k = -1;
            for (int i = 0; i < arc.Length; i++) { if (arc[i] <= v + EPS) k = i; else break; }
            return k;
        }

        private static int FirstIndexAtOrAfter(double[] arc, double v)
        {
            for (int i = 0; i < arc.Length; i++) if (arc[i] >= v - EPS) return i;
            return -1;
        }

        /// <summary>同 <see cref="ChordDeviation"/>，但在已经算好弧长表的折线上跑（省掉反复 Subspan）。</summary>
        private static double ChordDeviationBetween(Polyline span, double[] arc, double arcA, double arcB)
        {
            P3 a = PointAtArcLengthNoScan(span, arc, arcA);
            P3 b = PointAtArcLengthNoScan(span, arc, arcB);
            double worst = 0;
            for (int i = 0; i < span.Count; i++)
            {
                if (arc[i] <= arcA + EPS || arc[i] >= arcB - EPS) continue;
                double t, d;
                ClosestPointOnSegment(a, b, span.Points[i], out t, out d);
                if (d > worst) worst = d;
            }
            return worst;
        }

        private static P3 PointAtArcLengthNoScan(Polyline span, double[] arc, double want)
        {
            double total = arc[arc.Length - 1];
            if (want <= 0) return span.Points[0];
            if (want >= total) return span.Points[span.Count - 1];
            for (int i = 1; i < span.Count; i++)
            {
                if (arc[i] >= want)
                {
                    double seg = arc[i] - arc[i - 1];
                    double t = seg <= EPS ? 0 : (want - arc[i - 1]) / seg;
                    return P3.Lerp(span.Points[i - 1], span.Points[i], Clamp(t, 0, 1));
                }
            }
            return span.Points[span.Count - 1];
        }

        private static int ClampIndex(int i, int count)
        {
            if (i < 0) return 0;
            if (i >= count) return count - 1;
            return i;
        }

        /// <summary>
        /// 线段 a→b 是否**穿过**一条折线（不含只在端点上相碰）。跨路/跨建筑检测用它。
        /// 与 <see cref="SegmentsIntersect"/> 一样是严格相交：贴着走、端点相触都不算穿越。
        /// </summary>
        public static bool SegmentCrossesPolyline(P3 a, P3 b, Polyline line)
        {
            P3 x;
            return SegmentCrossesPolyline(a, b, line, out x);
        }

        /// <summary>
        /// 同上，并把**第一个**交点经 <paramref name="crossing"/> 交出来（没有交点时给出 a→b 的中点，
        /// 调用方在返回 false 时不会去读它）。相交点位置是「路口」与「横穿路段中段」的唯一区分手段。
        ///
        /// ⚠ 只做逐段严格相交会漏掉一种真实形状：**折线的顶点正好落在 a→b 上、折线从它两侧穿过去**。
        /// 这种形状下相邻两段各自与 a→b 的叉积必有一个是 0，`> EPS && < -EPS` 那两道同时不成立，
        /// 于是「正对着路口横穿马路」被判成没穿越，而同样这条线挪开 1 厘米又判穿越 ——
        /// 跨越判据（需求 3「产业区不许跨路」）出现位置相关的随机漏判。补下面这道顶点闸。
        /// </summary>
        public static bool SegmentCrossesPolyline(P3 a, P3 b, Polyline line, out P3 crossing)
        {
            crossing = P3.Lerp(a, b, 0.5);
            if (line == null || line.Count < 2) return false;
            for (int i = 0; i + 1 < line.Count; i++)
            {
                if (!SegmentsIntersect(a, b, line.Points[i], line.Points[i + 1])) continue;
                if (TrySegmentIntersection(a, b, line.Points[i], line.Points[i + 1], out crossing)) return true;
                return true;    // 判了相交但解不出点（两段近平行）：位置留着中点，判定结果不变
            }
            for (int j = 1; j + 1 < line.Count; j++)
            {
                if (!VertexOnSegmentThrough(a, b, line, j)) continue;
                crossing = line.Points[j];
                return true;
            }
            return false;
        }

        /// <summary>
        /// 折线的第 <paramref name="j"/> 个内部顶点是否被线段 a→b 「直穿而过」：
        /// ① 顶点到 a→b 的**垂直距离**在 EPS 内（用叉积除以 |ab| 归一，容差不随线段长度漂移）；
        /// ② 顶点落在线段**开区间**内（贴在 a 或 b 上只是相触，不是穿越）；
        /// ③ 前后相邻顶点分居 a→b 两侧（同侧就是折线在这里掉头，没穿过去）。
        /// </summary>
        private static bool VertexOnSegmentThrough(P3 a, P3 b, Polyline line, int j)
        {
            P3 v = line.Points[j];
            double rX = b.X - a.X;
            double rY = b.Y - a.Y;
            double lenSq = rX * rX + rY * rY;
            if (lenSq <= EPS * EPS) return false;
            if (System.Math.Abs(CrossZ(a, b, v)) > EPS * System.Math.Sqrt(lenSq)) return false;
            double t = ((v.X - a.X) * rX + (v.Y - a.Y) * rY) / lenSq;
            if (t <= EPS || t >= 1 - EPS) return false;
            double prev = CrossZ(a, b, line.Points[j - 1]);
            double next = CrossZ(a, b, line.Points[j + 1]);
            return (prev > EPS && next < -EPS) || (prev < -EPS && next > EPS);
        }

        /// <summary>
        /// 两线段求交点（参数式解一次 2×2 线性方程组）。退化（平行/共线）返回 false。
        /// 判定「是否相交」用 <see cref="SegmentsIntersect"/> 就够了，这里多给一个**位置**，
        /// 因为跨越判据要看相交发生在被穿线的哪一段：路口附近不算跨越。
        /// </summary>
        public static bool TrySegmentIntersection(P3 p1, P3 p2, P3 p3, P3 p4, out P3 hit)
        {
            hit = P3.Lerp(p1, p2, 0.5);
            double rX = p2.X - p1.X, rY = p2.Y - p1.Y;
            double sX = p4.X - p3.X, sY = p4.Y - p3.Y;
            double denom = rX * sY - rY * sX;
            if (Math.Abs(denom) <= 1e-12) return false;
            double qpx = p3.X - p1.X, qpy = p3.Y - p1.Y;
            double t = (qpx * sY - qpy * sX) / denom;
            double u = (qpx * rY - qpy * rX) / denom;
            if (t < 0 || t > 1 || u < 0 || u > 1) return false;
            hit = P3.Lerp(p1, p2, t);
            return true;
        }

        /// <summary>点是否落在折线一侧的闭合多边形内（折线首尾自动连线；只在四点以上的轮廓上有意义）。</summary>
        public static bool PointInClosedPolyline(P3 p, Polyline line)
        {
            if (line == null || line.Count < 3) return false;
            bool inside = false;
            int n = line.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                P3 a = line.Points[i];
                P3 b = line.Points[j];
                if ((a.Y > p.Y) != (b.Y > p.Y))
                {
                    double t = (p.Y - a.Y) / (b.Y - a.Y);
                    if (p.X < a.X + t * (b.X - a.X)) inside = !inside;
                }
            }
            return inside;
        }

        /// <summary>
        /// 折线（按 <paramref name="closed"/> 决定是否补回首点）的包围盒。
        /// 调用方一律走这里而不是 <c>new Polyline(list).Bounds()</c>：闭合那条线一旦忘了补，
        /// 盒子会漏掉收尾那段，后面所有「先比盒子再算距离」的粗筛就静默失效。
        /// </summary>
        public static Box2 CurveBounds(IList<P3> pts, bool closed)
        {
            Box2 b = Box2.Empty();
            if (pts == null || pts.Count == 0) return b;
            int n = closed && pts.Count > 1 ? pts.Count + 1 : pts.Count;
            for (int i = 0; i < n; i++) b.Expand(pts[i == pts.Count ? 0 : i]);
            return b;
        }

        /// <summary>
        /// <paramref name="pts"/> 的每个点到 <paramref name="curve"/> 的最大最近点距离（豪斯多夫单向）。
        /// 用在「两条边界算不算同一条线」上：<b>逐点比序号是错的</b> —— 同一形状可以有两种点数
        /// （重描一遍会把每条长边切得更密），比序号会得出「不一样」，于是每次跟随都重写一遍存档，
        /// 并且点数还会一轮轮往上涨。按形状比才既能认出「其实没变」，也才不会把区域拖坏。
        /// </summary>
        public static double MaxDeviationToCurve(IList<P3> pts, IList<P3> curve, bool curveClosed)
        {
            if (pts == null || pts.Count == 0) return 0;
            if (curve == null || curve.Count < 2) return double.PositiveInfinity;
            Polyline line = new Polyline(CloseCurve(curve, curveClosed));
            double worst = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                double d = ClosestPointOnPolylineProbe(line, pts[i], out _, out _, out _, out _);
                if (d > worst) worst = d;
            }
            return worst;
        }

        /// <summary>
        /// 两条曲线互为「离对方不超过 tol」⇒ 同一个形状（闭合集按闭合比）。
        /// 走 <see cref="WithinLimit"/> 而不是先算精确最近点再比：前者带逐段包围盒早退、
        /// 且命中即返回，两遍扫下来绝大多数点根本不需要开方。这个判定每次跟随都对整圈点跑，
        /// 慢一倍的写法实机就是掉帧。
        /// </summary>
        public static bool CurvesAgree(IList<P3> a, bool aClosed, IList<P3> b, bool bClosed, double tol)
        {
            if (a == null || b == null) return ReferenceEquals(a, b);
            if (a.Count == 0 || b.Count == 0) return a.Count == b.Count;
            Polyline lb = new Polyline(CloseCurve(b, bClosed));
            for (int i = 0; i < a.Count; i++) if (!WithinLimit(lb, a[i], tol)) return false;
            Polyline la = new Polyline(CloseCurve(a, aClosed));
            for (int i = 0; i < b.Count; i++) if (!WithinLimit(la, b[i], tol)) return false;
            return true;
        }

        private static List<P3> CloseCurve(IList<P3> curve, bool closed)
        {
            List<P3> l = new List<P3>(curve.Count + 1);
            for (int i = 0; i < curve.Count; i++) l.Add(curve[i]);
            if (closed && l.Count > 1) l.Add(l[0]);
            return l;
        }

        /// <summary>
        /// 「这条描边是否在 limit 之内」的廉价判定，带真早退。
        /// 全城路网几万条边，描边与贴合预算里最贵的浪费是「离得很远还老老实实算完最近点」，
        /// 所以粗筛顺序固定为：包围盒距离 → 本判定 → 才做精确最近点计算。
        /// </summary>
        public static bool WithinLimit(Polyline line, P3 p, double limit)
        {
            if (line == null || line.Count < 2) return false;
            double limit2 = limit * limit;
            for (int i = 0; i + 1 < line.Count; i++)
            {
                P3 a = line.Points[i];
                P3 b = line.Points[i + 1];
                // 线段包围盒粗判：先排除掉绝大多数明显不可能的段，再算精确距离。
                double minX = a.X < b.X ? a.X : b.X;
                double maxX = a.X < b.X ? b.X : a.X;
                double minY = a.Y < b.Y ? a.Y : b.Y;
                double maxY = a.Y < b.Y ? b.Y : a.Y;
                if (p.X < minX - limit || p.X > maxX + limit || p.Y < minY - limit || p.Y > maxY + limit) continue;
                double t;
                double d;
                ClosestPointOnSegment(a, b, p, out t, out d);
                if (d * d <= limit2) return true;
            }
            return false;
        }
    }
}
