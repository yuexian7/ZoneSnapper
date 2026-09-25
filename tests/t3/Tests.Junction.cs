using System;
using System.Collections.Generic;
using ZoneSnapper.Engine;

namespace ZoneSnapper.T3
{
    /// <summary>
    /// J 段：路口归并（<c>Engine/JunctionKit.cs</c> + <c>TraceKit.ClampToJunctions</c>）。
    ///
    /// 这一段钉的是第八轮反馈 5（附两张截图，红圈圈住的就是同一路口上那一圈节点）：
    /// 「路口的节点很多 —— 路口中心有，偏上偏下偏左偏右、人行道处都有，导致自动贴合路径在路口
    /// 经常选一个不在中心点的节点拐弯」。他要的是两件事，顺序不能反：
    ///  ① **一个路口先只有一个代表点**，任何车道方向连过来都经过它；
    ///  ② 这个点要不要真占一格节点，之后再按「直线不放节点」判（他原话「毕竟直线不需要设置节点」）。
    ///
    /// ①是结构性修法：走线在路口那一格的顶点由 <see cref="WorldSnapshot.NodePoint"/> 给出、
    /// 交付前再统一夹一次（ClampToJunctions）。老写法直接采各条边自己的折线端点 ——
    /// 而游戏把每条边的线采到**它自己那个节点**上（副网络/人行道的节点在中心旁边几米），
    /// 于是"拐在哪一格"取决于当时用的是哪条边，同一条边界每次重算都可能抖几米。
    /// ②本来就由既有契约保证（第六轮反馈 1：直线段不补自动节点），J11 在这里复验一次：
    /// 直穿过路口时那个中心点**不该**留下一个节点。
    /// </summary>
    internal static partial class Tests
    {
        internal static class Junctions
        {
            /// <summary>
            /// 一个十字路口 + 游戏从它身上分出去的两种"副接头"：
            ///  · n5 在中心**正北 8 米**（截图里"偏上"那一格：它与中心之间是一条 8 米的短边）；
            ///  · n5 自己又接一条 60 米的边出去 ⇒ 它度数 2，属于路口但**不是**主接头。
            /// 四条臂各长 100 米（&gt; MERGE_SPAN），所以不会把别的路口拽进同一组。
            /// </summary>
            private static WorldSnapshot CrossWithSide(out int centre, out int sideNode, out int sideFar)
            {
                var w = new WorldSnapshot();
                centre = 0; sideNode = 5; sideFar = 6;
                w.Nodes.Add(Fix.Node(0, 100, 0));        // 主接头：游戏给这个路口放的位置
                w.Nodes.Add(Fix.Node(1, 0, 0));
                w.Nodes.Add(Fix.Node(2, 200, 0));
                w.Nodes.Add(Fix.Node(3, 100, 100));
                w.Nodes.Add(Fix.Node(4, 100, -100));
                w.Nodes.Add(Fix.Node(5, 100, 8));        // "偏上"那一格
                w.Nodes.Add(Fix.Node(6, 160, 8));        // 副网络那一头的远端
                w.Edges.Add(Fix.Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(100, 0), Fix.XY(0, 0), 10)));
                w.Edges.Add(Fix.Edge(1, NetKind.Road, 0, 2, Fix.Straight(Fix.XY(100, 0), Fix.XY(200, 0), 10)));
                w.Edges.Add(Fix.Edge(2, NetKind.Road, 0, 3, Fix.Straight(Fix.XY(100, 0), Fix.XY(100, 100), 10)));
                w.Edges.Add(Fix.Edge(3, NetKind.Road, 0, 4, Fix.Straight(Fix.XY(100, 0), Fix.XY(100, -100), 10)));
                w.Edges.Add(Fix.Edge(4, NetKind.Road, 0, 5, Fix.Straight(Fix.XY(100, 0), Fix.XY(100, 8), 2)));    // 8 米短边
                w.Edges.Add(Fix.Edge(5, NetKind.Road, 5, 6, Fix.Straight(Fix.XY(100, 8), Fix.XY(160, 8), 6)));   // 60 米
                w.BuildAdjacency();
                return w;
            }

            public static void Run()
            {
                var cfg = ModConfig.CreateDefault();

                Harness.Section("JunctionKit 一个路口只有一个代表点（J1~J4，第八轮反馈 5）", () =>
                {
                    int centre, sideNode, sideFar;
                    var w = CrossWithSide(out centre, out sideNode, out sideFar);
                    w.EnsureJunctions();

                    Check.Int("J1 整个路口（含偏北那格副接头）只归并出 1 个路口", 1, w.Junctions.Count);
                    var j = w.Junctions[0];
                    Check.Int("J1 成员 = 主接头 + 那条 8 米短边那一格", 2, j.MemberCount);
                    Check.True("J1 成员下标升序（确定性：重算两次必须是同一张表）", () => j.Nodes[0] < j.Nodes[1]);
                    Check.Int("J1 度数取成员里最高的那一格（四条臂 + 路口内部那条 8 米短边）", 5, j.Degree);
                    Check.True("J2 中心点 = **主接头**的坐标，一点没被副接头往北拽（截图里那 8 米）", () =>
                        Harness.Near(j.Center, Fix.XY(100, 0), 1e-9));
                    Check.Int("J3 偏北那格也属于这个路口", 0, w.JunctionOf(sideNode));
                    Check.Int("J3 60 米开外那一格不属于任何路口（它是路的中段端点）", -1, w.JunctionOf(sideFar));
                    Check.True("J4 走线取坐标时副接头给出的是中心点，不是它自己的位置（结构性修法）", () =>
                        Harness.Near(w.NodePoint(sideNode), Fix.XY(100, 0), 1e-9)
                        && Harness.Near(w.NodePoint(centre), Fix.XY(100, 0), 1e-9));
                    Check.True("J4 不属于路口的节点仍旧用自己的坐标（路上分段点没有「中心」这一说）", () =>
                        Harness.Near(w.NodePoint(sideFar), Fix.XY(160, 8), 1e-9));
                    Check.True("J4 同一份输入两次建表逐字段全等（跟随每帧重算才敢用）", () =>
                    {
                        int[] map;
                        var again = JunctionKit.Build(w, out map);
                        if (again.Count != w.Junctions.Count) return false;
                        for (int i = 0; i < again.Count; i++)
                        {
                            var x = w.Junctions[i];
                            var y = again[i];
                            if (x.MemberCount != y.MemberCount) return false;
                            if (x.Degree != y.Degree) return false;
                            if (!Harness.Near(x.Center, y.Center, 0)) return false;
                            if (x.Signature != y.Signature) return false;
                        }
                        return true;
                    });
                });

                Harness.Section("JunctionKit 判据的边界：宁可少并，不能多并（J5~J8）", () =>
                {
                    // —— J5 两个相邻路口不许并成一个：中间那条边 20 米 > MERGE_SPAN(15)。
                    var w = new WorldSnapshot();
                    w.Nodes.Add(Fix.Node(0, 0, 0));
                    w.Nodes.Add(Fix.Node(1, 0, 100));
                    w.Nodes.Add(Fix.Node(2, 0, -100));
                    w.Nodes.Add(Fix.Node(3, 20, 0));
                    w.Nodes.Add(Fix.Node(4, 120, 0));
                    w.Nodes.Add(Fix.Node(5, 20, 100));
                    w.Nodes.Add(Fix.Node(6, 20, -100));
                    w.Edges.Add(Fix.Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(0, 100), 5)));
                    w.Edges.Add(Fix.Edge(1, NetKind.Road, 0, 2, Fix.Straight(Fix.XY(0, 0), Fix.XY(0, -100), 5)));
                    w.Edges.Add(Fix.Edge(2, NetKind.Road, 0, 3, Fix.Straight(Fix.XY(0, 0), Fix.XY(20, 0), 2)));
                    w.Edges.Add(Fix.Edge(3, NetKind.Road, 3, 4, Fix.Straight(Fix.XY(20, 0), Fix.XY(120, 0), 10)));
                    w.Edges.Add(Fix.Edge(4, NetKind.Road, 3, 5, Fix.Straight(Fix.XY(20, 0), Fix.XY(20, 100), 5)));
                    w.Edges.Add(Fix.Edge(5, NetKind.Road, 3, 6, Fix.Straight(Fix.XY(20, 0), Fix.XY(20, -100), 5)));
                    w.BuildAdjacency();
                    w.EnsureJunctions();
                    Check.Int("J5 隔 20 米的两个路口没被并成一个（并错=边界从一个点跳出去，肉眼可见）", 2, w.Junctions.Count);
                    Check.True("J5 两个中心点各自落在自己那一格上", () =>
                        Harness.Near(w.Junctions[0].Center, Fix.XY(0, 0), 1e-9)
                        && Harness.Near(w.Junctions[1].Center, Fix.XY(20, 0), 1e-9));

                    // —— J6 同一条直路上的分段点（度数 2）根本不是路口 ⇒ 后面 J11 才谈得上「直线不放节点」。
                    var solo = Fix.OneStraightEdge(100, 10);
                    solo.EnsureJunctions();
                    Check.Int("J6 直路中间不建路口", 0, solo.Junctions.Count);
                    Check.Int("J6 断头路的端点也不因为「只有一条边」而被当成路口", -1, solo.JunctionOf(0));

                    // —— J7 只有人行步道交汇：不是路口（第六轮反馈 9 那份白名单同一口径）。
                    var pw = new WorldSnapshot();
                    pw.Nodes.Add(Fix.Node(0, 0, 0));
                    pw.Nodes.Add(Fix.Node(1, 100, 0));
                    pw.Nodes.Add(Fix.Node(2, 0, 100));
                    pw.Nodes.Add(Fix.Node(3, 0, -100));
                    pw.Edges.Add(Fix.Edge(0, NetKind.Path, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(100, 0), 5)));
                    pw.Edges.Add(Fix.Edge(1, NetKind.Path, 0, 2, Fix.Straight(Fix.XY(0, 0), Fix.XY(0, 100), 5)));
                    pw.Edges.Add(Fix.Edge(2, NetKind.Path, 0, 3, Fix.Straight(Fix.XY(0, 0), Fix.XY(0, -100), 5)));
                    pw.BuildAdjacency();
                    pw.EnsureJunctions();
                    Check.Int("J7 三条步道交汇也不建路口（反馈 1「空地上吸到旁边一个节点」的来源之一）", 0, pw.Junctions.Count);

                    // —— J8 地下那一半（第八轮反馈 3）：隧道与下沉段既不计度数、也不参与归并。
                    var tw = new WorldSnapshot();
                    tw.Nodes.Add(Fix.Node(0, 0, 0));
                    tw.Nodes.Add(Fix.Node(1, 100, 0));
                    tw.Nodes.Add(Fix.Node(2, 0, 100));
                    tw.Nodes.Add(Fix.Node(3, 0, -100));
                    var t1 = Fix.Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(100, 0), 5));
                    var t2 = Fix.Edge(1, NetKind.Road, 0, 2, Fix.Straight(Fix.XY(0, 0), Fix.XY(0, 100), 5));
                    var t3 = Fix.Edge(2, NetKind.Road, 0, 3, Fix.Straight(Fix.XY(0, 0), Fix.XY(0, -100), 5));
                    t2.Tunnel = true;
                    t3.LowElevation = -4.0;         // 明挖下沉段：同样在地下
                    tw.Edges.Add(t1); tw.Edges.Add(t2); tw.Edges.Add(t3);
                    tw.BuildAdjacency();
                    tw.EnsureJunctions();
                    Check.Int("J8 两条腿在地下 ⇒ 地面合法度数只剩 1 ⇒ 这个路口根本不成立", 0, tw.Junctions.Count);
                    Check.False("J8 隧道边不参与归并（Mergeable 那一层就挡掉）", () => JunctionKit.Mergeable(t2));
                    Check.False("J8 下沉段（LowElevation=-4）同样不参与归并", () => JunctionKit.Mergeable(t3));
                    var open = Fix.Edge(9, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(100, 0), 5));
                    open.Elevated = true;
                    Check.True("J8 高架照旧参与（第五轮反馈 4：高架也要能贴）", () => JunctionKit.Mergeable(open));
                });

                Harness.Section("走线在路口只认中心点（J9~J11，反馈 5 的端到端）", () =>
                {
                    int centre, sideNode, sideFar;
                    var w = CrossWithSide(out centre, out sideNode, out sideFar);
                    var tuning = Fix.TraceTuning(AreaTier.District);

                    // —— J9 西臂中点 → 北臂中点：拐弯那一格必须是中心点。
                    var a = Fix.OnEdge(Fix.XY(50, 0), 0, 50);
                    var b = Fix.OnEdge(Fix.XY(100, 50), 2, 50);
                    var r = TraceKit.Trace(a, b, w, cfg, tuning, Fix.EmptyCtx());
                    Check.True("J9 走线成功（两端都在路上，用不上续接）", () =>
                        r.UsedNetwork && r.Fallback == TraceFallback.None && !r.Continued);
                    Check.True("J9 拐弯那一格就是中心点 (100,0)", () => HasNear(r.Points, Fix.XY(100, 0)));
                    Check.Int("J9 本来就在中心点上 ⇒ junc 不计数（这一间只数「被挪过来」的格）", 0, r.Clamped);

                    // —— J10 从副网络那条 60 米边连过来：以前拐在 (100,8)，现在必须拐在中心。
                    var c = Fix.OnEdge(Fix.XY(130, 8), 5, 30);
                    var d = Fix.OnEdge(Fix.XY(100, -50), 3, 50);
                    var r2 = TraceKit.Trace(c, d, w, cfg, tuning, Fix.EmptyCtx());
                    Check.True("J10 副网络那侧连过来也拐在同一个中心点（截图里那个 8 米台阶）", () =>
                        r2.UsedNetwork && r2.Fallback == TraceFallback.None && HasNear(r2.Points, Fix.XY(100, 0)));
                    Check.True("J10 结果里不许再有任何一格停在偏心节点 (100,8) 上", () =>
                        !HasNear(r2.Points, Fix.XY(100, 8)));
                    // 实机统计行的 junc= 就是从这个数累加上去的（TraceResult.Clamped）：
                    // 偏心那一格被挪到中心 ⇒ 计 1 格。玩家截图那种 8 米台阶改没改掉，第九轮看这一间账。
                    Check.Int("J10 被挪到中心点的格数 = 1（这一条边只有一个路口拐弯）", 1, r2.Clamped);
                    Check.True("J10 幂等：同一对端点重描两次逐格全等（不许每次抖一下）", () =>
                    {
                        var again = TraceKit.Trace(c, d, w, cfg, tuning, Fix.EmptyCtx());
                        if (again.Points.Count != r2.Points.Count) return false;
                        for (int i = 0; i < again.Points.Count; i++)
                            if (!Harness.Near(again.Points[i], r2.Points[i], 1e-12)) return false;
                        return true;
                    });

                    // —— J11 反馈 5 的第二半：中心点先算出来，**再**判它要不要占一格节点。
                    //     西臂直通东臂（同一条路被游戏拆成两段、中间那个接头是个四岔路口）：
                    //     形状上根本没拐弯 ⇒ 一个节点都不该放（第六轮反馈 1 那条契约在这里继续成立）。
                    var wa = Fix.OnEdge(Fix.XY(50, 0), 0, 50);
                    var eb = Fix.OnEdge(Fix.XY(150, 0), 1, 50);
                    var straight = TraceKit.Trace(wa, eb, w, cfg, tuning, Fix.EmptyCtx());
                    Check.True("J11 直穿过路口 ⇒ 走线成功", () =>
                        straight.UsedNetwork && straight.Fallback == TraceFallback.None);
                    Check.True("J11 但没有拐弯就不放节点：中心点 (100,0) 不占一格（「毕竟直线不需要设置节点」）", () =>
                        !HasNear(straight.Points, Fix.XY(100, 0)));
                });
            }

            private static bool HasNear(List<P3> pts, P3 want)
            {
                if (pts == null) return false;
                for (int i = 0; i < pts.Count; i++) if (pts[i].DistanceTo(want) <= 1e-6) return true;
                return false;
            }
        }
    }
}
