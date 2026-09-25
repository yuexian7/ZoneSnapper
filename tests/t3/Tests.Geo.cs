using System;
using System.Collections.Generic;
using ZoneSnapper.Engine;

namespace ZoneSnapper.T3
{
    internal static partial class Tests
    {
        /// <summary>GeoKit：一切上层判定的地基，这里出错的话描边与贴合的断言全都是空中楼阁。</summary>
        internal static class Geo
        {
            public static void Run()
            {
                Harness.Section("GeoKit 线段/折线基础（G）", () =>
                {
                    var a = Fix.XY(0, 0);
                    var b = Fix.XY(10, 0);

                    // —— 最近点参数夹取：两个方向的外侧都必须夹到端点，否则「贴到线段延长线上」会把点甩到路外面
                    double t, d;
                    P3 hit = GeoKit.ClosestPointOnSegment(a, b, Fix.XY(-5, 5), out t, out d);
                    Check.True("G 起点外侧 t==0（夹到起点，不给负参数）", () => t == 0.0);
                    Check.True("G 起点外侧 吸附点==线段起点", () => Harness.Near(hit, a, 1e-12));
                    Check.Close("G 起点外侧 距离=√50", 7.0710678118654755, d, 1e-9);

                    hit = GeoKit.ClosestPointOnSegment(a, b, Fix.XY(15, 5), out t, out d);
                    Check.True("G 终点外侧 t==1", () => t == 1.0);
                    Check.True("G 终点外侧 吸附点==线段终点", () => Harness.Near(hit, b, 1e-12));
                    Check.Close("G 终点外侧 距离=√50", 7.0710678118654755, d, 1e-9);

                    hit = GeoKit.ClosestPointOnSegment(a, b, Fix.XY(5, -3), out t, out d);
                    Check.True("G 段内 t==0.5", () => Harness.Near(t, 0.5, 1e-12));
                    Check.True("G 段内 吸附点=(5,0)", () => Harness.Near(hit, Fix.XY(5, 0), 1e-12));
                    Check.Close("G 段内 距离=3", 3, d, 1e-12);

                    hit = GeoKit.ClosestPointOnSegment(a, b, Fix.XY(3, 0), out t, out d);
                    Check.Close("G 落在线上的点 距离=0", 0, d, 1e-12);
                    Check.True("G 退化线段（a==b）不炸且不 NaN", () =>
                    {
                        double tt, dd;
                        P3 hh = GeoKit.ClosestPointOnSegment(a, a, Fix.XY(3, 4), out tt, out dd);
                        return tt == 0 && dd == 5 && Harness.Near(hh, a, 1e-12);
                    });

                    // —— 折线最近点：段号 + 弧长（描边切子段靠的就是这个弧长）
                    var lshape = Fix.Poly(0, 0, 10, 0, 10, 10);
                    P3 best; int seg; double st, al, dist;
                    GeoKit.ClosestPointOnPolyline(lshape, Fix.XY(10.5, 5), out best, out seg, out st, out al, out dist);
                    Check.Int("G 折线最近点 落在第 1 段", 1, seg);
                    Check.Close("G 折线最近点 弧长=15（拐点后 5 米）", 15, al, 1e-9);
                    Check.Close("G 折线最近点 距离=0.5", 0.5, dist, 1e-12);
                    GeoKit.ClosestPointOnPolyline(lshape, Fix.XY(3, -1), out best, out seg, out st, out al, out dist);
                    Check.Int("G 折线最近点 落在第 0 段", 0, seg);
                    Check.Close("G 折线最近点 弧长=3", 3, al, 1e-9);
                    Check.True("G 单点折线：返回该点、段号 -1", () =>
                    {
                        var one = Fix.Poly(7, 9);
                        P3 bb; int s2; double t2, a2, d2;
                        GeoKit.ClosestPointOnPolyline(one, Fix.XY(7, 5), out bb, out s2, out t2, out a2, out d2);
                        return s2 == -1 && Harness.Near(bb, Fix.XY(7, 9), 1e-12) && Harness.Near(d2, 4, 1e-12);
                    });
                    Check.True("G 空折线不抛异常", () =>
                    {
                        P3 bb; int s2; double t2, a2, d2;
                        GeoKit.ClosestPointOnPolyline(new Polyline(), Fix.XY(0, 0), out bb, out s2, out t2, out a2, out d2);
                        return s2 == -1 && double.IsPositiveInfinity(d2);
                    });

                    // —— 弧长取点 + 夹取
                    var hundred = Fix.Straight(Fix.XY(0, 0), Fix.XY(100, 0), 10);
                    double ca;
                    Check.True("G PointAtArcLength(25)=(25,0)", () => Harness.Near(GeoKit.PointAtArcLength(hundred, 25, out ca), Fix.XY(25, 0), 1e-9));
                    Check.True("G PointAtArcLength(-10) 夹到 0", () => Harness.Near(GeoKit.PointAtArcLength(hundred, -10, out ca), Fix.XY(0, 0), 1e-9) && ca == 0);
                    Check.True("G PointAtArcLength(1e9) 夹到末端", () => Harness.Near(GeoKit.PointAtArcLength(hundred, 1e9, out ca), Fix.XY(100, 0), 1e-9) && Harness.Near(ca, 100, 1e-9));

                    // —— Subspan：弧长子段的起止与内部顶点
                    var sp = GeoKit.Subspan(hundred, 20, 50);
                    Check.Int("G Subspan(20,50) 首点=20", 1, sp.Count > 0 && Harness.Near(sp.Points[0], Fix.XY(20, 0), 1e-9) ? 1 : 0);
                    Check.Int("G Subspan(20,50) 末点=50", 1, sp.Count > 0 && Harness.Near(sp.Points[sp.Count - 1], Fix.XY(50, 0), 1e-9) ? 1 : 0);
                    Check.Int("G Subspan(20,50) 含内部顶点 30/40 → 共 4 点", 4, sp.Count);
                    Check.Close("G Subspan(20,50) 弧长=30", 30, sp.Length2D(), 1e-9);
                    Check.True("G Subspan 顶点 x 单调不减", () =>
                    {
                        for (int i = 1; i < sp.Count; i++) if (sp.Points[i].X < sp.Points[i - 1].X - 1e-9) return false;
                        return true;
                    });
                    var full = GeoKit.Subspan(hundred, 0, 100);
                    Check.Int("G Subspan(0,total) = 整条折线", hundred.Count, full.Count);
                    var clampSpan = GeoKit.Subspan(hundred, -10, 1e9);
                    Check.Int("G Subspan 超界夹到整条", hundred.Count, clampSpan.Count);
                    // 端点落在段内部（不是顶点上）时才算真正考验 Subspan：一条 10 米直路取 [2,8]
                    var two = GeoKit.Subspan(Fix.Straight(Fix.XY(0, 0), Fix.XY(10, 0), 1), 2, 8);
                    Check.Int("G Subspan(2,8) 只应出两端 1 个点都不该多（文档：含两端插值点的 [a,b] 部分）", 2, two.Count);
                    Check.Close("G Subspan(2,8) 弧长必须=6（不得越过 b=8 再折回来）", 6, two.Length2D(), 1e-9);
                    Check.True("G Subspan 顶点全落在 [a,b] 区间内（越界点会让边界先冲过头再折回）", () =>
                    {
                        for (int i = 0; i < two.Count; i++) if (two.Points[i].X < 2 - 1e-9 || two.Points[i].X > 8 + 1e-9) return false;
                        return true;
                    });
                    var same = GeoKit.Subspan(hundred, 40, 40);
                    Check.Int("G Subspan(a==b) 退化 1 点", 1, same.Count);
                    Check.True("G Subspan(a==b) 位置正确", () => Harness.Near(same.Points[0], Fix.XY(40, 0), 1e-9));
                    // 反向弧长：本层刻意不处理（调用方 FillFromPolyline/AppendSpan 自己排序 lo/hi）。
                    // 注意 GeoKit.cs:277 的注释写着「arcA>arcB 时按跨过端点处理」，与实现不符 —— 见本次报告。
                    var rev = GeoKit.Subspan(hundred, 50, 20);
                    Check.Int("G Subspan(a>b) 返回空（反向由调用方排序）", 0, rev.Count);
                    Check.Int("G Subspan 对 <2 点折线返回空", 0, GeoKit.Subspan(Fix.Poly(0, 0), 0, 5).Count);
                });

                Harness.Section("GeoKit 相交/面积/包围盒/粗筛（G2）", () =>
                {
                    Check.True("G2 严格 X 形相交", () => GeoKit.SegmentsIntersect(Fix.XY(0, 0), Fix.XY(10, 10), Fix.XY(0, 10), Fix.XY(10, 0)));
                    Check.False("G2 端点相触不算相交（共线/相接是正常拼接）", () => GeoKit.SegmentsIntersect(Fix.XY(0, 0), Fix.XY(10, 0), Fix.XY(10, 0), Fix.XY(20, 0)));
                    Check.False("G2 共线重叠不算相交（文档口径：严格相交）", () => GeoKit.SegmentsIntersect(Fix.XY(0, 0), Fix.XY(10, 0), Fix.XY(4, 0), Fix.XY(14, 0)));
                    Check.False("G2 分离的两段不相交", () => GeoKit.SegmentsIntersect(Fix.XY(0, 0), Fix.XY(1, 0), Fix.XY(0, 5), Fix.XY(1, 5)));
                    Check.Close("G2 逆时针正方形有向面积=+100", 100, GeoKit.SignedArea2D(new List<P3> { Fix.XY(0, 0), Fix.XY(10, 0), Fix.XY(10, 10), Fix.XY(0, 10) }), 1e-9);
                    Check.Close("G2 顶点序反转 ⇒ 面积反号", -100, GeoKit.SignedArea2D(new List<P3> { Fix.XY(0, 10), Fix.XY(10, 10), Fix.XY(10, 0), Fix.XY(0, 0) }), 1e-9);
                    Check.Close("G2 三角形面积", 12.5, GeoKit.SignedArea2D(new List<P3> { Fix.XY(0, 0), Fix.XY(5, 0), Fix.XY(0, 5) }), 1e-9);
                    Check.Close("G2 共线环面积 0（薄片退化）", 0, GeoKit.SignedArea2D(new List<P3> { Fix.XY(0, 0), Fix.XY(10, 0), Fix.XY(20, 0) }), 1e-9);
                    Check.Close("G2 少于 3 点面积 0", 0, GeoKit.SignedArea2D(new List<P3> { Fix.XY(0, 0), Fix.XY(1, 0) }), 1e-12);
                    Check.Close("G2 十字积符号：左转>0", 1, GeoKit.CrossZ(Fix.XY(0, 0), Fix.XY(1, 0), Fix.XY(1, 1)) > 0 ? 1 : -1, 1e-12);
                    Check.Close("G2 十字积符号：右转<0", -1, GeoKit.CrossZ(Fix.XY(0, 0), Fix.XY(1, 0), Fix.XY(1, -1)) > 0 ? 1 : -1, 1e-12);

                    var line = Fix.Poly(0, 0, 10, 0, 10, 10);
                    // —— WithinLimit 必须与精确距离同口径：粗筛一旦漏掉真候选，玩家就会「贴不上」
                    int checkedN = 0, skipped = 0;
                    double[] limits = { 2.3137, 6.1741 };
                    foreach (double lim in limits)
                    {
                        for (int gx = -4; gx <= 14; gx++)
                        {
                            for (int gy = -4; gy <= 14; gy++)
                            {
                                P3 q = Fix.XY(gx * 1.0 + 0.137, gy * 1.0 + 0.411);
                                double exact = GeoKit.DistanceFromPoint(line, q);
                                if (Math.Abs(exact - lim) < 1e-6) { skipped++; continue; }
                                bool fast = GeoKit.WithinLimit(line, q, lim);
                                bool slow = exact <= lim;
                                checkedN++;
                                if (fast != slow)
                                {
                                    Check.Bad("G2 WithinLimit 与精确距离不一致", "q=" + q + " lim=" + lim + " exact=" + exact, "fast=" + fast);
                                    goto sweepDone;
                                }
                            }
                        }
                    }
                    sweepDone:
                    Check.True("G2 WithinLimit 与精确距离在 " + checkedN + " 个网格点上完全一致（跳过临界 " + skipped + " 个）", () => checkedN > 200 && skipped == 0);
                    Check.False("G2 短极限：远处的线判外", () => GeoKit.WithinLimit(line, Fix.XY(100, 100), 5));
                    Check.True("G2 点在线上：判内", () => GeoKit.WithinLimit(line, Fix.XY(10, 3), 0.5));
                    Check.False("G2 <2 点折线一律判外（WithinLimit 的粗筛前置假设）", () => GeoKit.WithinLimit(Fix.Poly(3, 3), Fix.XY(3, 3), 100));

                    var box = Fix.Poly(0, 0, 10, 0, 10, 10).Bounds();
                    Check.Close("G2 Box2 内含点距离 0", 0, box.DistanceTo(Fix.XY(4, 4)), 1e-12);
                    Check.Close("G2 Box2 外角点距离 = 到最近边的垂直距离 4", 4, box.DistanceTo(Fix.XY(14, 7)), 1e-9);
                    Check.Close("G2 Box2 斜外角点(12,13) 距离=√13", Math.Sqrt(13), box.DistanceTo(Fix.XY(12, 13)), 1e-9);
                    Check.True("G2 Box2 相交判定", () => box.Intersects(Fix.Poly(9, 9, 20, 20).Bounds()) && !box.Intersects(Fix.Poly(11, 11, 20, 20).Bounds()));
                    Check.True("G2 Box2 膨胀后包含外点", () => box.Inflated(2).Contains(Fix.XY(-1, -1)) && !box.Contains(Fix.XY(-1, -1)));
                });
            }
        }
    }
}
