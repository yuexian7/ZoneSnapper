using System;
using System.Collections.Generic;
using ZoneSnapper.Engine;

namespace ZoneSnapper.T3
{
    internal static partial class Tests
    {
        /// <summary>CurveKit：两点之间的「少节点弯曲」（需求 9 的替代实现）。</summary>
        internal static class Curve
        {
            public static void Run()
            {
                Harness.Section("CurveKit 弧化与平滑（C）", () =>
                {
                    var a = Fix.XYH(0, 0, 0);
                    var b = Fix.XYH(100, 0, 10);

                    Check.Empty("C segments=0 ⇒ 一个点都不给（等价于不弯曲）", CurveKit.Arc(a, b, 0.15, 0));
                    Check.Empty("C segments<0 ⇒ 同样为空", CurveKit.Arc(a, b, 0.15, -3));
                    Check.Empty("C 两点重合 ⇒ 空（不许造出一堆重合点）", CurveKit.Arc(Fix.XY(5, 5), Fix.XY(5, 5), 0.15, 3));

                    var arc3 = CurveKit.Arc(a, b, 0.1, 3);
                    Check.Int("C segments=3 ⇒ 正好 3 点（不含两端）", 3, arc3.Count);
                    Check.True("C 三个点全都不在直连线上", () =>
                    {
                        for (int i = 0; i < arc3.Count; i++) if (arc3[i].DistanceTo(Fix.XY(arc3[i].X, 0)) < 1e-6) return false;
                        return true;
                    });
                    Check.True("C 中点正好落在拱顶 (50,10,5)：曲线真的「经过」mid 而不是被控制点拽鼓", () =>
                        Harness.Near(arc3[1], Fix.XYH(50, 10, 5), 1e-9));
                    Check.True("C 左右对称：首末两点关于中垂线镜像（平面坐标）", () =>
                        Harness.Near(arc3[0].X, 100 - arc3[2].X, 1e-9) && Harness.Near(arc3[0].Y, arc3[2].Y, 1e-9));
                    Check.Close("C 端点距离对称", arc3[0].DistanceTo(a), arc3[2].DistanceTo(b), 1e-9);
                    Check.True("C 顺序沿 a→b 前进（x 单调递增，投影才不会打结）", () =>
                    {
                        for (int i = 1; i < arc3.Count; i++) if (arc3[i].X <= arc3[i - 1].X) return false;
                        return true;
                    });
                    Check.True("C 高度沿 a.H→b.H 单调插值（标记不能陷进地里）", () =>
                    {
                        for (int i = 1; i < arc3.Count; i++) if (arc3[i].H <= arc3[i - 1].H) return false;
                        return arc3[0].H > a.H && arc3[arc3.Count - 1].H < b.H;
                    });
                    var arc1 = CurveKit.Arc(a, b, 0.1, 1);
                    Check.Int("C segments=1 ⇒ 只给拱顶", 1, arc1.Count);
                    Check.True("C segments=1 的拱顶与 segments=3 的中点重合", () => Harness.Near(arc1[0], arc3[1], 1e-9));
                    var bulge0 = CurveKit.Arc(a, b, 0, 3);
                    Check.True("C bulge=0 ⇒ 退化成直线上三段（弯度为 0）", () =>
                    {
                        for (int i = 0; i < bulge0.Count; i++) if (Math.Abs(bulge0[i].Y) > 1e-9) return false;
                        return bulge0.Count == 3;
                    });
                    var neg = CurveKit.Arc(a, b, -0.1, 3);
                    Check.True("C bulge 取负 ⇒ 往另一侧弯（凸向可选）", () => neg[1].Y < 0 && Harness.Near(neg[1], Fix.XYH(50, -10, 5), 1e-9));
                    Check.Int("C 每段弧的中间点数严格等于 segments（节点预算能被 cfg 精确控制）", 8, CurveKit.Arc(a, b, 0.15, 8).Count);
                });

                Harness.Section("CurveKit Smooth 点数与保形（C2）", () =>
                {
                    var square = new List<P3> { Fix.XY(0, 0), Fix.XY(100, 0), Fix.XY(100, 100), Fix.XY(0, 100) };
                    var closed = CurveKit.Smooth(square, true, 3);
                    Check.Int("C2 闭合：点数 = 段数(4) × segments(3)", 12, closed.Count);
                    var open = CurveKit.Smooth(square, false, 3);
                    Check.Int("C2 开放：点数 = 段数(3) × segments(3)", 9, open.Count);
                    Check.Int("C2 segments=5 闭合 ⇒ 4×5", 20, CurveKit.Smooth(square, true, 5).Count);
                    Check.Empty("C2 segments=0 ⇒ 空", CurveKit.Smooth(square, true, 0));
                    Check.Empty("C2 少于 3 点不做平滑", CurveKit.Smooth(new List<P3> { Fix.XY(0, 0), Fix.XY(1, 1) }, true, 3));
                    Check.Empty("C2 null ⇒ 空不抛", CurveKit.Smooth(null, true, 3));
                    Check.True("C2 平滑结果不越出原方形的外扩框（不许甩飞）", () =>
                    {
                        var box = Fix.Poly(0, 0, 100, 0, 100, 100, 0, 100).Bounds().Inflated(30);
                        for (int i = 0; i < closed.Count; i++) if (!box.Contains(closed[i])) return false;
                        return true;
                    });
                    Check.True("C2 平滑把直角切圆了：中位点到最近原边的距离 > 0", () =>
                    {
                        // 每个 span 的第一个插值点应当落在角附近、离两条原边都不为 0
                        var line = Fix.Poly(0, 0, 100, 0);
                        double minD = 1e18;
                        for (int i = 0; i < closed.Count; i++) minD = Math.Min(minD, Harness.DistToLine(line, closed[i]));
                        return minD > 1e-6;
                    });
                    var mono = new List<P3> { Fix.XY(0, 0), Fix.XY(10, 0), Fix.XY(20, 0), Fix.XY(30, 0) };
                    var ms = CurveKit.Smooth(mono, false, 4);
                    Check.True("C2 全共线输入 ⇒ 平滑后仍在直线上（不无中生有造弯）", () =>
                    {
                        for (int i = 0; i < ms.Count; i++) if (Math.Abs(ms[i].Y) > 1e-9) return false;
                        return ms.Count == 12;
                    });
                });
            }
        }

        /// <summary>
        /// IsoKit：游戏里没有现成的海岸线折线（FACT：NetToolSystem.cs:1811-1866 是在网格里现算湿/干质心），
        /// 我们自己抽等值线并缝合。阈值必须与游戏的 0.2f 同口径，否则「贴上海边」和「游戏认为的陆地」会打架。
        /// </summary>
        internal static class Iso
        {
            public static void Run()
            {
                Harness.Section("IsoKit 海岸等值线（I）", () =>
                {
                    Check.Close("I 阈值与游戏 SnapShoreline 的 0.2f 同口径", 0.2, IsoKit.WATER_LEVEL, 1e-12);

                    var f = Island(300, 10);      // 31×31 网格，半径 100 的圆岛泡在水里
                    var lines = IsoKit.BuildIsolines(f);
                    Check.True("I 有岛 ⇒ 至少一条等值线", () => lines.Count >= 1);
                    int total = 0;
                    for (int i = 0; i < lines.Count; i++) total += lines[i].Count;
                    Check.True("I 等值线总点数够多（圆周长 628，格边 10 ⇒ 约 60 段）", () => total > 30);
                    Check.True("I 每个交点的高度都正好是阈值 0.2（等值线定义）", () =>
                    {
                        for (int i = 0; i < lines.Count; i++)
                        {
                            var pts = lines[i].Points;
                            for (int k = 0; k < pts.Count; k++) if (Math.Abs(pts[k].H - IsoKit.WATER_LEVEL) > 1e-12) return false;
                        }
                        return true;
                    });
                    // 半径容差 0.5 米：10 米网格上对「只随半径变化」的场做线性插值，
                    // 交点离真圆最多差一个弦弓高 ≈ step²/(8r) ≈ 0.13 米，所以 0.5 已经是严格上界。
                    Check.True("I 每个交点的平面半径都 ≈100（±0.5 米，等值线确实是那条 h=0.2 的圆）", () =>
                    {
                        for (int i = 0; i < lines.Count; i++)
                        {
                            var pts = lines[i].Points;
                            for (int k = 0; k < pts.Count; k++)
                            {
                                double r = Math.Sqrt(pts[k].X * pts[k].X + pts[k].Y * pts[k].Y);
                                if (Math.Abs(r - 100) > 0.5) return false;
                            }
                        }
                        return true;
                    });
                    var longest = Longest(lines);
                    // 圆岛的海岸线就该是一条闭环；混进零长度碎片说明网格点恰好落在水位线上时鞍形分支配错了段
                    Check.True("I 一座岛 ⇒ 一条等值线（其余碎片会让海岸描边凭空多出断口）", () => lines.Count == 1);
                    Check.True("I 不许出现零长度退化段（重合点正是游戏三角化最怕的东西）", () =>
                    {
                        for (int i = 0; i < lines.Count; i++)
                        {
                            var pts = lines[i].Points;
                            if (pts.Count < 2) return false;
                            // 判「整条线缩成一个点」不能拿首尾比：闭环本来就该首尾同点（下一条断言正是要它）。
                            // 用总长判才对得上原意 —— 这条自己差点造出一个假失败。
                            if (lines[i].Length2D() <= 1e-9) return false;
                            for (int k = 1; k < pts.Count; k++) if (pts[k].DistanceTo(pts[k - 1]) <= 1e-12) return false;
                        }
                        return true;
                    });
                    Check.True("I 闭环首尾回到同一点（相邻线段缝合无缺口）", () =>
                    {
                        var pts = longest.Points;
                        return pts.Count > 4 && pts[0].DistanceTo(pts[pts.Count - 1]) < 1e-6;
                    });
                    Check.InRange("I 闭环周长 ≈ 2πr = 628", longest.Length2D(), 628 * 0.9, 628 * 1.1);
                    Check.False("I 等值线不自交（自交的海岸线会让区域三角化直接失败）", () => SimplifyKit.SelfIntersects(longest.Points));

                    var flat = Flat(20, 10, 0.5);
                    Check.Int("I 整片同高 ⇒ 零条等值线", 0, IsoKit.BuildIsolines(flat).Count);
                    var allWet = Flat(20, 10, 0.05);
                    Check.Int("I 整片都是水 ⇒ 零条", 0, IsoKit.BuildIsolines(allWet).Count);
                    Check.Int("I 网格太窄（1×N）⇒ 零条", 0, IsoKit.BuildIsolines(new ScalarField { Values = new double[] { 0.1, 0.3 }, Width = 1, Height = 2, StepX = 10, StepZ = 10 }).Count);
                    Check.Int("I Values=null ⇒ 零条不抛", 0, IsoKit.BuildIsolines(new ScalarField { Values = null, Width = 5, Height = 5 }).Count);
                    Check.Empty("I 无段可缝 ⇒ 空表", IsoKit.Stitch(null, 0));
                    Check.Empty("I 空段表 ⇒ 空表", IsoKit.Stitch(new List<IsoKit.Seg>(), 0));
                });

                Harness.Section("IsoKit 缝合器本身（I2）", () =>
                {
                    // 手工给 4 条首尾相接的段：必须缝成一条 5 点、首尾同点的闭环
                    var ring = new List<IsoKit.Seg>
                    {
                        new IsoKit.Seg { A = Fix.XY(0, 0), B = Fix.XY(1, 0) },
                        new IsoKit.Seg { A = Fix.XY(1, 0), B = Fix.XY(1, 1) },
                        new IsoKit.Seg { A = Fix.XY(1, 1), B = Fix.XY(0, 1) },
                        new IsoKit.Seg { A = Fix.XY(0, 1), B = Fix.XY(0, 0) },
                    };
                    var one = IsoKit.Stitch(ring, 0);
                    Check.Int("I2 闭环缝成 1 条", 1, one.Count);
                    Check.Int("I2 5 个点（闭合环重复首点）", 5, one[0].Count);
                    Check.True("I2 首尾同点", () => one[0].Points[0].DistanceTo(one[0].Points[4]) < 1e-12);
                    Check.True("I2 每相邻两点距离为 1（没有被打乱顺序）", () =>
                    {
                        for (int i = 1; i < one[0].Count; i++) if (Math.Abs(one[0].Points[i].DistanceTo(one[0].Points[i - 1]) - 1) > 1e-12) return false;
                        return true;
                    });

                    var disjoint = new List<IsoKit.Seg>
                    {
                        new IsoKit.Seg { A = Fix.XY(0, 0), B = Fix.XY(1, 0) },
                        new IsoKit.Seg { A = Fix.XY(50, 0), B = Fix.XY(51, 0) },
                    };
                    Check.Int("I2 互不相干的段 ⇒ 各成一条", 2, IsoKit.Stitch(disjoint, 0).Count);

                    var nearMiss = new List<IsoKit.Seg>
                    {
                        new IsoKit.Seg { A = Fix.XY(0, 0), B = Fix.XY(1, 0) },
                        new IsoKit.Seg { A = Fix.XY(1.3, 0), B = Fix.XY(2.3, 0) },
                    };
                    Check.Int("I2 0.3 米的缝：eps=1.0 时缝上（量化网格就是这个用途）", 1, IsoKit.Stitch(nearMiss, 1.0).Count);
                    Check.Int("I2 0.3 米的缝：eps=1e-4 时仍是两条（默认精度只用来吃浮点噪声）", 2, IsoKit.Stitch(nearMiss, 1e-4).Count);
                    Check.Int("I2 eps<0 视同默认精度", 1, IsoKit.Stitch(ring, -1).Count);
                });
            }

            /// <summary>半径 100 的圆岛：h = 0.35 - 0.0015·r ⇒ h=0.2 的等值线正好是 r=100 的圆。</summary>
            private static ScalarField Island(double extent, double step)
            {
                int n = (int)(extent * 2 / step) + 1;
                var v = new double[n * n];
                for (int z = 0; z < n; z++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        double wx = -extent + x * step;
                        double wz = -extent + z * step;
                        double r = Math.Sqrt(wx * wx + wz * wz);
                        v[z * n + x] = 0.35 - 0.0015 * r;
                    }
                }
                return new ScalarField
                {
                    Values = v,
                    Width = n,
                    Height = n,
                    StepX = step,
                    StepZ = step,
                    OriginX = -extent,
                    OriginZ = -extent,
                    StitchEpsilon = 0
                };
            }

            private static ScalarField Flat(int n, double step, double h)
            {
                var v = new double[n * n];
                for (int i = 0; i < v.Length; i++) v[i] = h;
                return new ScalarField { Values = v, Width = n, Height = n, StepX = step, StepZ = step };
            }

            private static Polyline Longest(List<Polyline> lines)
            {
                Polyline best = new Polyline();
                for (int i = 0; i < lines.Count; i++) if (lines[i].Length2D() > best.Length2D()) best = lines[i];
                return best;
            }
        }
    }
}
