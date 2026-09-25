using System;
using System.Collections.Generic;
using ZoneSnapper.Engine;

namespace ZoneSnapper.T3
{
    internal static partial class Tests
    {
        /// <summary>
        /// SimplifyKit：需求 9「节点少但形状在」的唯一保证。
        /// 砍太狠 = 玩家看到棱角分明的区域边界；砍不动 = 一个弯道几百个节点，游戏三角化与存档都吃亏。
        /// 所以这里两边都要钉住。
        /// </summary>
        internal static class Simplify
        {
            public static void Run()
            {
                Harness.Section("SimplifyKit RDP 保端点与坍缩（S）", () =>
                {
                    var hundred = Fix.Straight(Fix.XY(0, 0), Fix.XY(100, 0), 99);
                    Check.Int("S 输入 100 点共线", 100, hundred.Count);
                    var flat = SimplifyKit.Simplify(hundred.Points, 1.0);
                    Check.Int("S 100 个共线点 → 塌成 2", 2, flat.Count);
                    Check.True("S RDP 保首点", () => Harness.Near(flat[0], hundred.Points[0], 1e-12));
                    Check.True("S RDP 保末点", () => Harness.Near(flat[flat.Count - 1], hundred.Points[hundred.Count - 1], 1e-12));

                    var zig = Fix.Poly(0, 0, 50, 0, 50, 50, 100, 50, 100, 100);
                    var z1 = SimplifyKit.Simplify(zig.Points, 0.5);
                    Check.Int("S 直角阶梯在容差内一点不砍", zig.Count, z1.Count);
                    Check.Empty("S 空输入 → 空输出", SimplifyKit.Simplify(new List<P3>(), 1));
                    Check.Empty("S null 输入 → 空输出（不抛）", SimplifyKit.Simplify(null, 1));
                    Check.Int("S 2 点原样返回", 2, SimplifyKit.Simplify(Fix.Poly(0, 0, 3, 4).Points, 5).Count);
                    Check.Int("S 容差 0 = 不简化（原样返回）", 5, SimplifyKit.Simplify(zig.Points, 0).Count);
                    Check.Int("S 负容差 = 不简化", 5, SimplifyKit.Simplify(zig.Points, -1).Count);
                    // 容差越大点越少：这条不成立的话 FitToBudget 的放大循环就是无用功
                    int prev = int.MaxValue;
                    bool mono = true;
                    foreach (double tol in new double[] { 0.01, 0.5, 2, 8, 20, 60 })
                    {
                        int c = SimplifyKit.Simplify(QuarterCircle(100, 201).Points, tol).Count;
                        if (c > prev) mono = false;
                        prev = c;
                    }
                    Check.True("S 容差单调：越简化点越少（0.01→60 六档）", () => mono);
                });

                Harness.Section("SimplifyKit 四分之一圆保形（S2）", () =>
                {
                    const double TOL = 2.0;
                    var arc = QuarterCircle(100, 201);
                    var simp = SimplifyKit.Simplify(arc.Points, TOL);
                    double dev = MaxDeviation(arc.Points, simp);
                    Check.True("S2 弯道必须留下拐点（>2 点）", () => simp.Count > 2);
                    Check.InRange("S2 简化后形状误差 ≤ 容差", dev, 0, TOL);
                    Check.True("S2 不许过度简化：误差 ≥ 容差/4（否则与直线无异）", () => dev >= TOL / 4.0);
                    Check.InRange("S2 201 点压到 4..40 点", simp.Count, 4, 40);
                    Check.True("S2 首末点位置未动", () => Harness.Near(simp[0], arc.Points[0], 1e-9)
                        && Harness.Near(simp[simp.Count - 1], arc.Points[arc.Count - 1], 1e-9));
                    // 简化后的每个点都还该在圆弧本体上（半径 100 ± 采样误差）
                    double maxR = 0, minR = 1e18;
                    for (int i = 0; i < simp.Count; i++)
                    {
                        double r = Math.Sqrt(simp[i].X * simp[i].X + simp[i].Y * simp[i].Y);
                        if (r > maxR) maxR = r;
                        if (r < minR) minR = r;
                    }
                    Check.InRange("S2 保留点仍在半径 100 的弧上", minR, 99.9, 100.0001);
                    Check.InRange("S2 保留点最远也在弧上", maxR, 99.9999, 100.0001);
                    Check.False("S2 弯道不是直线（中间点到首末连线的距离>容差）", () =>
                    {
                        double t; double d;
                        GeoKit.ClosestPointOnSegment(arc.Points[0], arc.Points[arc.Count - 1], simp[simp.Count / 2], out t, out d);
                        return d <= TOL;
                    });
                });

                Harness.Section("SimplifyKit Dedupe 收尾（S3）", () =>
                {
                    Check.Empty("S3 空 → 空", SimplifyKit.Dedupe(new List<P3>(), 3));
                    Check.Int("S3 单点原样", 1, SimplifyKit.Dedupe(Fix.Poly(1, 2).Points, 3).Count);
                    var sparse = Fix.Straight(Fix.XY(0, 0), Fix.XY(10, 0), 10).Points;   // 0..10 每米一点
                    var dd = SimplifyKit.Dedupe(sparse, 3);
                    Check.True("S3 抽稀后仍含末点", () => Harness.Near(dd[dd.Count - 1], sparse[sparse.Count - 1], 1e-12));
                    bool spaced = true;
                    for (int i = 1; i < dd.Count; i++) if (dd[i].DistanceTo(dd[i - 1]) < 3 - 1e-9) spaced = false;
                    Check.True("S3 相邻点间距 ≥ minSpacing", () => spaced);
                    Check.Int("S3 间距 0 = 全保留", 11, SimplifyKit.Dedupe(sparse, 0).Count);
                    // 首尾同点（闭合环的重复尾点）：必须吃掉一个
                    var closedTwice = Fix.Poly(0, 0, 10, 0, 10, 10, 0, 0);
                    // Dedupe 只管「相邻间距」；「闭合环不重复首点」是 ProjectKit.BuildClosedRing 的活（P3 已测）
                    var cc = SimplifyKit.Dedupe(closedTwice.Points, 2);
                    Check.Int("S3 与首点重合的尾点：相邻间距够就不算重复（闭合去重不归本函数）", 4, cc.Count);
                    // 关键契约：整条链都太密时，宁可留首尾两点也不能把尾巴吃掉 ——
                    // 尾巴是「接上下一个手动节点」的那一点，丢了区域就闭合不上（本函数注释自己写的话）。
                    var allDense = Fix.Poly(0, 0, 1, 0, 2, 0);
                    var ad = SimplifyKit.Dedupe(allDense.Points, 5);
                    Check.True("S3 全链过密时末点仍是输入末点 (2,0)", () =>
                        ad.Count > 0 && Harness.Near(ad[ad.Count - 1], Fix.XY(2, 0), 1e-12));
                    var allDense4 = Fix.Poly(0, 0, 1, 0, 2, 0, 3, 0);
                    var ad4 = SimplifyKit.Dedupe(allDense4.Points, 5);
                    Check.True("S3 全链过密（4 点）末点仍是 (3,0)", () =>
                        ad4.Count > 0 && Harness.Near(ad4[ad4.Count - 1], Fix.XY(3, 0), 1e-12));
                });

                Harness.Section("SimplifyKit MergeCollinear 只删笔直点（S4）", () =>
                {
                    var straightChain = Fix.Straight(Fix.XY(0, 0), Fix.XY(100, 0), 20);
                    var mc = SimplifyKit.MergeCollinear(straightChain.Points, SimplifyKit.STRAIGHT_COS);
                    Check.Int("S4 21 个共线点合并成 2", 2, mc.Count);
                    Check.True("S4 合并保住两端", () => Harness.Near(mc[0], Fix.XY(0, 0), 1e-12) && Harness.Near(mc[1], Fix.XY(100, 0), 1e-12));

                    // 唯一折点在 (30,0)：转 30°，cos(30°)=0.866 ≪ cos(2°)=0.99939 ⇒ 绝不能删；
                    // 其余 (10,0)(20,0) 共线 ⇒ 该删。
                    var bend30 = Fix.Poly(0, 0, 10, 0, 20, 0, 30, 0, 40, 5.7735026918962584, 50, 11.547005383792517);
                    var b30 = SimplifyKit.MergeCollinear(bend30.Points, SimplifyKit.STRAIGHT_COS);
                    Check.Int("S4 30° 折角：6 点里只留折点与两端 = 3", 3, b30.Count);
                    Check.True("S4 30° 折点位置未动", () => ContainsNear(b30, Fix.XY(30, 0), 1e-9));
                    Check.True("S4 共线的 (10,0)(20,0) 被删掉", () => !ContainsNear(b30, Fix.XY(10, 0), 1e-9));
                    // 1° 折角：在 2° 门槛以内 ⇒ 该删
                    var bend1 = Fix.Poly(0, 0, 10, 0, 20, 0.17455064928617831, 30, 0.3491012985723566);
                    var b1 = SimplifyKit.MergeCollinear(bend1.Points, SimplifyKit.STRAIGHT_COS);
                    Check.True("S4 1° 折角视为笔直 → 中间点被删", () => b1.Count == 2);
                    Check.Int("S4 少于 3 点原样返回", 2, SimplifyKit.MergeCollinear(Fix.Poly(0, 0, 1, 1).Points, SimplifyKit.STRAIGHT_COS).Count);
                    Check.Empty("S4 null → 空", SimplifyKit.MergeCollinear(null, 0.99));
                    var heavy = SimplifyKit.MergeCollinear(Fix.Poly(0, 0, 10, 0, 10, 10, 10, 20, 20, 20).Points, SimplifyKit.STRAIGHT_COS);
                    Check.Int("S4 直角折线只吃掉共线的第 3 点", 4, heavy.Count);
                });

                Harness.Section("SimplifyKit 自交与退化检测（S5）", () =>
                {
                    var bowtie = Fix.Poly(0, 0, 10, 10, 10, 0, 0, 10);
                    Check.True("S5 四点多边形自交：蝴蝶结判为自交（两条边在 (5,5) 交叉）",
                        () => SimplifyKit.SelfIntersects(bowtie.Points));
                    var bowtie5 = Fix.Poly(0, 0, 10, 10, 10, 0, 0, 10, 0, -5);
                    Check.True("S5 五点多边形自交：蝴蝶结判为自交", () => SimplifyKit.SelfIntersects(bowtie5.Points));
                    var square = Fix.Poly(0, 0, 10, 0, 10, 10, 0, 10);
                    Check.False("S5 正方形（开放折线）不自交", () => SimplifyKit.SelfIntersects(square.Points));
                    var closedSquare = Fix.Poly(0, 0, 10, 0, 10, 10, 0, 10, 0, 0);
                    Check.False("S5 显式闭合的正方形不自交（首尾相接是合法的）", () => SimplifyKit.SelfIntersects(closedSquare.Points));
                    Check.False("S5 三点折线不可能自交", () => SimplifyKit.SelfIntersects(Fix.Poly(0, 0, 1, 1, 2, 0).Points));
                    Check.False("S5 null 不抛", () => SimplifyKit.SelfIntersects(null));
                    var ring8 = new List<P3> { Fix.XY(0, 0), Fix.XY(10, 10), Fix.XY(10, 0), Fix.XY(0, 10) };
                    Check.True("S5 环自交：8 字", () => SimplifyKit.RingSelfIntersects(ring8));
                    var ring8b = new List<P3> { Fix.XY(0, 0), Fix.XY(20, 0), Fix.XY(20, 20), Fix.XY(0, 20), Fix.XY(0, 30), Fix.XY(10, 10), Fix.XY(30, 10), Fix.XY(20, 30) };
                    Check.True("S5 环自交：内凹打结的八边形", () => SimplifyKit.RingSelfIntersects(ring8b));
                    var ringSq = new List<P3> { Fix.XY(0, 0), Fix.XY(10, 0), Fix.XY(10, 10), Fix.XY(0, 10) };
                    Check.False("S5 环自交：凸正方形为假", () => SimplifyKit.RingSelfIntersects(ringSq));
                    var ringCcw = new List<P3> { Fix.XY(0, 0), Fix.XY(10, 0), Fix.XY(10, 10), Fix.XY(5, 11), Fix.XY(0, 10) };
                    Check.False("S5 环自交：凸五边形为假", () => SimplifyKit.RingSelfIntersects(ringCcw));
                    Check.False("S5 环自交：少于 4 点为假", () => SimplifyKit.RingSelfIntersects(new List<P3> { Fix.XY(0, 0), Fix.XY(1, 0), Fix.XY(0, 1) }));
                    Check.True("S5 10×10 正方形不算退化（面积 100 ≫ minArea 4）", () => SimplifyKit.IsDegenerateRing(ringSq, 4) == false);
                    var sliver = new List<P3> { Fix.XY(0, 0), Fix.XY(200, 0), Fix.XY(200, 0.005), Fix.XY(0, 0.005) };
                    Check.True("S5 200×0.005 薄片判为退化（原模组 0.2.3 的那个 bug）", () => SimplifyKit.IsDegenerateRing(sliver, 4));
                    Check.True("S5 共线环判为退化", () => SimplifyKit.IsDegenerateRing(new List<P3> { Fix.XY(0, 0), Fix.XY(10, 0), Fix.XY(20, 0) }, 1));
                });

                Harness.Section("SimplifyKit FitToBudget 预算与容差（S6）", () =>
                {
                    var circle = FullCircle(100, 401);
                    double used;
                    // 输入本身就够少 ⇒ 容差不该被放大（放大意味着白掉精度）
                    var small = Fix.Poly(0, 0, 50, 60, 100, 0);
                    var r0 = SimplifyKit.FitToBudget(small.Points, 0.5, 2, 60, out used);
                    Check.Close("S6 未超预算时容差保持原值", 0.5, used, 1e-12);
                    Check.Int("S6 未超预算时点数不增", 3, r0.Count);

                    var r = SimplifyKit.FitToBudget(circle.Points, 1.0, 2.0, 12, out used);
                    Check.True("S6 超预算 ⇒ 容差被放大（used>原始）", () => used > 1.0);
                    Check.InRange("S6 收敛到 maxNodes 之内", r.Count, 2, 12);
                    Check.True("S6 结果首点=输入首点、末点=输入末点（预算不能把两端换掉）", () =>
                        Harness.Near(r[0], circle.Points[0], 1e-9) && Harness.Near(r[r.Count - 1], circle.Points[circle.Count - 1], 1e-9));
                    var r2 = SimplifyKit.FitToBudget(circle.Points, 1.0, 2.0, 60, out used);
                    Check.True("S6 预算放宽 ⇒ 点数不减（约束更弱 ⇒ 结果更细）", () => r2.Count >= r.Count);
                    Check.Close("S6 预算够用 ⇒ 容差保持 1.0", 1.0, used, 1e-12);
                    var empty = SimplifyKit.FitToBudget(new List<P3>(), 1.0, 2.0, 10, out used);
                    Check.Empty("S6 空输入 → 空输出", empty);
                    Check.Close("S6 空输入容差保持", 1.0, used, 1e-12);
                    var single = SimplifyKit.FitToBudget(Fix.Poly(3, 4).Points, 1.0, 2.0, 4, out used);
                    Check.Int("S6 单点输入原样返回", 1, single.Count);
                    // maxNodes 小到不可能达成时也不能返回 null / 不能抛
                    var hope = SimplifyKit.FitToBudget(circle.Points, 0.5, 0.0, 2, out used);
                    Check.True("S6 极端预算仍返回可用结果（不为 null）", () => hope != null && hope.Count >= 2);
                });
            }

            // ———— 本组小工具 ————

            /// <summary>整圆采样：周长 628，容差 1 时 RDP 给 ~22 点，用来造「必然超预算」的输入。</summary>
            private static Polyline FullCircle(double r, int n)
            {
                var l = new Polyline();
                for (int i = 0; i < n; i++)
                {
                    double th = 2 * Math.PI * i / (n - 1);
                    l.Add(new P3(r * Math.Cos(th), r * Math.Sin(th), 0));
                }
                return l;
            }

            /// <summary>半径 R 的四分之一圆弧（(R,0) → (0,R)），n 个采样点。</summary>
            private static Polyline QuarterCircle(double r, int n)
            {
                var l = new Polyline();
                for (int i = 0; i < n; i++)
                {
                    double th = (Math.PI / 2) * (i / (double)(n - 1));
                    l.Add(new P3(r * Math.Cos(th), r * Math.Sin(th), 0));
                }
                return l;
            }

            /// <summary>原折线各点到简化折线的最大偏离（RDP 的正确性方向：新线不许离老线太远）。</summary>
            private static double MaxDeviation(IList<P3> src, List<P3> simp)
            {
                if (simp == null || simp.Count == 0) return double.PositiveInfinity;
                var line = new Polyline(simp);
                double max = 0;
                for (int i = 0; i < src.Count; i++)
                {
                    double d = GeoKit.DistanceFromPoint(line, src[i]);
                    if (d > max) max = d;
                }
                return max;
            }

            private static bool ContainsNear(List<P3> pts, P3 p, double eps)
            {
                for (int i = 0; i < pts.Count; i++) if (pts[i].NearlyEquals(p, eps)) return true;
                return false;
            }
        }
    }
}
