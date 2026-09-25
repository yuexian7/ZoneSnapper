using System;
using System.Collections.Generic;

namespace ZoneSnapper.Engine
{
    /// <summary>
    /// 一个路口（交叉口）。
    ///
    /// 【为什么要有这个类型】第八轮反馈 5（附两张截图）：「路口的节点很多 —— 路口中心有，
    /// 路口中心偏右偏上偏左偏下甚至人行道处等等都可能有节点，导致自动贴合路径在路口处经常选择
    /// 不在中心点的节点就拐弯」。玩家要的是两件事，分开做才对：
    ///  ① **一个路口只有一个代表点**，无论从哪个车道方向连过来，都先经过这个点再去别的方向；
    ///  ② 这个点**要不要真的一格节点**，之后再按「直线不放节点」的规则判（他原话：
    ///     「然后再去判断这个点是否需要设置节点（毕竟直线不需要设置节点）」）。
    /// 老实现里没有 ①：走线用的接头坐标就是 <c>GraphNode.Pos</c>，而一个路口的成员节点来自
    /// 好几套结构（主网络节点 + 副网络/人行道节点 + 重叠的 SubNet 节点 + 游戏按
    /// <c>m_NodeOffset</c>/<c>m_MinNodeOffset</c> 拆出来的分段点）⇒ 同一路口有多个「合法接头」，
    /// A* 挑中哪个取决于哪条边先进邻接表，于是拐弯点会偏几米 —— 症状就是他截图里那一圈黄点。
    /// </summary>
    public sealed class Junction
    {
        /// <summary>这个路口的唯一代表点。走线与吸附候选都用它，不再用成员节点各自的位置。</summary>
        public P3 Center;

        /// <summary>成员图节点下标（升序，保证同样输入永远同样输出 —— 确定性是硬要求）。</summary>
        public readonly List<int> Nodes = new List<int>();

        /// <summary>成员里最高的那个度数（判「这是不是真路口」用的那一格的度数）。</summary>
        public int Degree;

        /// <summary>成员节点数。= 1 就是最常见的「一个路口一个节点」，中心点即该节点原位置。</summary>
        public int MemberCount { get { return Nodes.Count; } }

        /// <summary>几何签名：没变 ⇒ 中心点可以复用（跟随增量大算靠它，见 WorldSnapshot 的签名口径）。</summary>
        public long Signature;
    }

    /// <summary>
    /// 路口归并。纯几何 + 纯图，不碰任何游戏类型 ⇒ 在离线回归壳里可以直接断言（J 段）。
    ///
    /// 【判据抄的是游戏自己那条】游戏给路口打的标是「这个接头上接住了**几条不同的 net 实体**」：
    /// FACT：research/decompiled/Game.Net/CompositionSelectSystem.cs:469 <c>GetNodeFlags(...)</c> 里
    /// <c>:516 int num3 = 0</c> 数的就是接入的不同 net 实体数，<c>:748 if (num3 >= 2)
    /// handednessFlags.m_General |= CompositionFlags.General.Intersection;</c>
    /// （:758-770 给「一条路岔出去」那种 T 形也补上这个标）。
    /// 我们的 <see cref="MIN_SEED_DEGREE"/> 是它的等价形式：一个接头接住 ≥3 条**合法**边 ⇔
    /// 它连着 ≥2 条别的 net 实体（进来的那条 + 至少两条别的方向）。
    ///
    /// 【为什么还要按边长归并】游戏侧「一个路口 = 一个主节点」只在主拓扑成立
    /// （FACT：research/decompiled/Game.Net/Node.cs:10 <c>float3 m_Position</c> 一个节点一个位置；
    ///  副网络与重叠节点另算 —— 见 <c>Game.Net/SecondaryLane.cs</c>、<c>SubNet.cs</c>、
    ///  以及 <c>Game.Prefabs/NetGeometryData.cs:31/:38</c> 那两个节点偏移量把一条路拆成多段的做法）。
    /// 于是同一路口在我们的快照里可能是 2~5 个节点。归并条件取「两个节点之间那条边很短」：
    /// 路口内部的连接段本来就短（几米到十几米），而两个真路口之间至少隔着一个街区的宽度。
    /// </summary>
    public static class JunctionKit
    {
        /// <summary>
        /// 归并用的最长边（米）。取值理由：
        ///  · 下界要盖住「一条 4 车道 + 两侧人行道的路交叉口内部那段」——整幅路宽可以到 25~30 米，
        ///    游戏自己的节点偏移量 <c>m_NodeOffset</c> 也在这个量级；
        ///  · 上界不能吃到「两个相邻路口」——城市路网里两个路口之间最短也要几十米（再短就是畸形路）。
        /// 取 15 米：宁可**少并**（两个路口各自一个中心点，走线仍然稳定，只是没合并成同一个点），
        /// 也不能**多并**（把一条街上两个路口并成一个 ⇒ 边界从一个点跳出去，形状肉眼可见地错）。
        /// </summary>
        public const double MERGE_SPAN = 15.0;

        /// <summary>「真路口」的最低度数（合法边数）。2 只是同一条路的分段点，不是路口。</summary>
        public const int MIN_SEED_DEGREE = 3;

        /// <summary>
        /// 这条边可以参与路口归并吗？只放**白名单里的地面网络**：
        ///  · 隧道不参与（它在地下，跟地面路口不是同一个「点」；第八轮反馈 3）；
        ///  · 埠头/水上不参与；建筑/地块内部路不参与（第六轮反馈 9 那份名单同一口径）；
        ///  · 只认道路与轨道（步行路会把两个真路口用一串短边连起来 —— 人行道网比车行道密得多）。
        /// </summary>
        public static bool Mergeable(GraphEdge e)
        {
            if (e == null) return false;
            if (e.Underground || e.OnWater || e.Internal) return false;   // 第八轮反馈 3：隧道与开槽下沉段都不参与（Underground 含两者）
            return e.Kind == NetKind.Road || e.Kind == NetKind.Rail;
        }

        /// <summary>这条边的长度（米）：优先用采样时算好的 <see cref="GraphEdge.Length"/>，没有再量折线。</summary>
        private static double EdgeSpan(GraphEdge e)
        {
            if (e == null) return double.PositiveInfinity;
            if (e.Length > 0) return e.Length;
            if (e.Line == null || e.Line.Count < 2) return double.PositiveInfinity;
            return e.Line.Length2D();
        }

        /// <summary>
        /// 建路口表。
        /// <paramref name="junctionOf"/> 输出「图节点下标 → 路口下标」，-1 = 这个节点不属于任何路口
        /// （同一条路中间的分段点、断头路端点都是这种）——走线在 -1 的地方仍然用节点原坐标。
        /// </summary>
        public static List<Junction> Build(WorldSnapshot world, out int[] junctionOf)
        {
            junctionOf = null;
            List<Junction> list = new List<Junction>();
            if (world == null || world.Nodes == null || world.Nodes.Count == 0) return list;

            int n = world.Nodes.Count;

            // —— 1) 合法度数：只数白名单网络边。
            //      不用 GraphNode.Degree（那是采样器按「所有边」算的，会把步道/内部路算进来，
            //      于是「两条人行道交叉」也会被判成路口）。
            int[] degree = new int[n];
            for (int e = 0; e < world.Edges.Count; e++)
            {
                GraphEdge ge = world.Edges[e];
                if (!Mergeable(ge)) continue;
                if (ge.StartNode < 0 || ge.StartNode >= n || ge.EndNode < 0 || ge.EndNode >= n) continue;
                degree[ge.StartNode]++;
                if (ge.EndNode != ge.StartNode) degree[ge.EndNode]++;     // 自环只加一次
            }

            // —— 2) 并查集：短而合法的边把两端并到一起。
            int[] parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            for (int e = 0; e < world.Edges.Count; e++)
            {
                GraphEdge ge = world.Edges[e];
                if (!Mergeable(ge)) continue;
                int a = ge.StartNode, b = ge.EndNode;
                if (a < 0 || a >= n || b < 0 || b >= n || a == b) continue;
                if (EdgeSpan(ge) > MERGE_SPAN) continue;
                Union(parent, a, b);
            }

            // —— 3) 分组（按根下标），组内最高度数 >= MIN_SEED_DEGREE 才算路口。
            Dictionary<int, List<int>> groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                if (world.Nodes[i] == null) continue;
                int r = Find(parent, i);
                List<int> g;
                if (!groups.TryGetValue(r, out g)) { g = new List<int>(); groups[r] = g; }
                g.Add(i);
            }

            junctionOf = new int[n];
            for (int i = 0; i < n; i++) junctionOf[i] = -1;

            // 分组枚举顺序按根的升序，保证同一份输入永远得到同一张表（确定性：走线不许在
            // 两次重描之间换中心点，否则玩家看到的是「同一条边界每次重算都轻微抖一下」）。
            List<int> roots = new List<int>(groups.Keys);
            roots.Sort();
            for (int k = 0; k < roots.Count; k++)
            {
                List<int> members = groups[roots[k]];
                members.Sort();
                int maxDeg = 0;
                for (int i = 0; i < members.Count; i++) if (degree[members[i]] > maxDeg) maxDeg = degree[members[i]];
                if (maxDeg < MIN_SEED_DEGREE) continue;

                Junction j = new Junction();
                j.Degree = maxDeg;
                for (int i = 0; i < members.Count; i++) j.Nodes.Add(members[i]);

                // —— 4) 中心点：**只取成员里的「主接头」**（度数 >= MIN_SEED_DEGREE 的那些）做平均。
                //      为什么不是全成员平均：副网络/人行道那些成员度数低、位置偏（就在玩家截图里
                //      「中心偏上偏下偏左偏右」那几个位置），把它们算进来会把中心点往路边拽几米 ——
                //      那正是这条反馈要修掉的症状。主接头自己就是游戏给这个路口放的位置
                //      （FACT：Game.Net/Node.cs:10），一个主接头时中心点 = 它的原坐标，一个字节都不挪。
                double sx = 0, sy = 0, sh = 0;
                int prim = 0, bestDeg = -1;
                for (int i = 0; i < members.Count; i++)
                {
                    int mi = members[i];
                    if (degree[mi] < MIN_SEED_DEGREE) continue;
                    P3 p = world.Nodes[mi].Pos;
                    sx += p.X; sy += p.Y; sh += p.H; prim++;
                    if (degree[mi] > bestDeg) bestDeg = degree[mi];
                }
                if (prim > 0)
                {
                    // 高度取「最高度数那一格」的，不用平均：高架与地面路重叠时，平均出来的 H
                    // 是一个两边都不是的中间值（第八轮反馈 8 那一档同一口径：平局要有确定赢家）。
                    P3 ref0 = world.Nodes[Best(members, degree)].Pos;
                    j.Center = new P3(sx / prim, sy / prim, ref0.H);
                }
                else
                {
                    j.Center = world.Nodes[members[0]].Pos;
                }

                unchecked
                {
                    long sig = 1469598103934665603L;
                    for (int i = 0; i < members.Count; i++)
                        sig = (sig ^ world.Nodes[members[i]].Signature) * 1099511628211L;
                    j.Signature = sig;
                }

                int idx = list.Count;
                list.Add(j);
                for (int i = 0; i < members.Count; i++) junctionOf[members[i]] = idx;
            }
            return list;
        }

        /// <summary>成员里度数最高的那个（度数相同取下标小的 —— 又是确定性）。</summary>
        private static int Best(List<int> members, int[] degree)
        {
            int best = members[0];
            for (int i = 1; i < members.Count; i++)
            {
                if (degree[members[i]] > degree[best]) best = members[i];
            }
            return best;
        }

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
            return i;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra == rb) return;
            if (ra < rb) parent[rb] = ra; else parent[ra] = rb;     // 小的当根：与枚举顺序无关
        }
    }
}
