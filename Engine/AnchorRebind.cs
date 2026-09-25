using System;
using System.Collections.Generic;

namespace ZoneSnapper.Engine
{
    /// <summary>
    /// 把跨帧持有的锚点搬回「当前这一份世界快照」的下标空间。
    ///
    /// 【这一层为什么必须存在（v0.1.2 第三轮实机的根因）】
    /// 手动栈里的 <see cref="PlacedNode.Edge"/> / <see cref="PlacedNode.GraphNode"/> 是**某一次快照里的数组下标**，
    /// 而 EnsureWorld 每当地走廊长大一点就重抓一次快照（四叉树遍历顺序、走廊边界都会变 ⇒ 下标全洗牌）。
    /// 于是老锚点要么越界（TraceKit 在 `a.Edge >= world.Edges.Count` 直接 NotOnNetwork 退回直线），
    /// 要么指向另一条路（描出鬼线）。实机表现就是「游标明明会吸住，但边界永远是直线」：
    /// snap=2907 / traceHit=17 / fallback=119。
    ///
    /// 【为什么用签名而不是「再吸一次」】签名是 GameSide 用 `Entity.Index + Entity.Version + 该实体的参考坐标`
    /// 算出来的（FACT：WorldSampler.SignatureOf），同一条路在两次抓取之间不变 ⇒ 签名相同就是同一条路。
    /// 只有签名找不到（那条路真被改了或拆了）才退回「按位置重新吸附」。
    ///
    /// 【一条硬规矩：位置绝不挪】栈里这些点是玩家已经点下、并且已经落进游戏那张表的几何。
    /// 重绑改的是「它挂在哪条线的哪个弧长上」，不是它在哪。所有分支最后都会把 Pos 按回原值
    /// （T3 的 X11 段就是钉这一条）。要挪位置的只有 FollowKit 那条明确的「重描」路径，那是另一回事。
    /// </summary>
    public static class AnchorRebind
    {
        /// <summary>一次重绑的账（进统计行，实机靠它判「重绑到底有没有在工作」）。</summary>
        public sealed class Stats
        {
            public int Total;
            /// <summary>靠签名搬回原对象（换了下标而已）。</summary>
            public int Rebound;
            /// <summary>签名找不到了 ⇒ 按位置重新吸附（位置仍不变）。</summary>
            public int Resnapped;
            /// <summary>重新吸附也没命中 ⇒ 这一格退回自由点（它的边只能走直线）。</summary>
            public int Lost;
        }

        /// <summary>
        /// 单个锚点重绑。<paramref name="n"/> 的位置原样带出；返回的锚点一定与 <paramref name="world"/> 同坐标系。
        /// </summary>
        public static PlacedNode Node(PlacedNode n, WorldSnapshot world, ModConfig cfg, ResolvedTuning tuning, Stats stats)
        {
            if (world == null) return n;
            if (stats != null) stats.Total++;

            PlacedNode r = n;
            bool moved = false;
            // 「签名认出来了」与「下标变了」是两件事：认出来了就必须用认出来的那条线（哪怕下标没变），
            // 认不出来才可以走下面的「按位置重新吸附」。混成一条的话，每次重抓快照都会把所有锚点
            // 重新吸一遍 —— 半径内同时有两条路时，重吸会把锚点从一条换到另一条，
            // 表现就是「同一区域每按一次 F6 边界微微不同」（需求 4 的确定性先没了）。
            bool resolved = false;

            switch (n.Kind)
            {
                case SnapKind.NetNode:
                {
                    int gn = world.IndexOfNode(n.Signature);
                    if (gn >= 0)
                    {
                        resolved = true;
                        r.GraphNode = gn;
                        r.Edge = SnapKit.NearestEdgeOf(world, gn);
                        r.Arc = r.Edge >= 0 ? SnapKit.ArcOfNode(world, r.Edge, gn) : 0;
                        moved = r.GraphNode != n.GraphNode || r.Edge != n.Edge;
                    }
                    break;
                }
                case SnapKind.NetCentre:
                case SnapKind.NetSide:
                {
                    int e = world.IndexOfEdge(n.Signature);
                    if (e >= 0)
                    {
                        resolved = true;
                        r.Edge = e;
                        r.Arc = ArcOfPosition(r, world.Edges[e]);
                        moved = r.Edge != n.Edge;
                    }
                    break;
                }
                case SnapKind.AreaBorderSame:
                case SnapKind.AreaBorderOther:
                {
                    int b = world.IndexOfBorder(n.Signature);
                    if (b >= 0)
                    {
                        resolved = true;
                        r.Edge = AnchorCode.Border(b);
                        r.Arc = ArcOfLine(r, world.Borders[b].Line);
                        moved = r.Edge != n.Edge;
                    }
                    break;
                }
                case SnapKind.MapTileBorder:
                {
                    int t = world.IndexOfMapTile(n.Signature);
                    if (t >= 0)
                    {
                        resolved = true;
                        r.Edge = AnchorCode.Tile(t);
                        r.Arc = ArcOfLine(r, world.MapTiles[t].Line);
                        moved = r.Edge != n.Edge;
                    }
                    break;
                }
                case SnapKind.ObjectSide:
                {
                    int o = world.IndexOfObject(n.Signature);
                    if (o >= 0)
                    {
                        resolved = true;
                        r.Edge = AnchorCode.Object(o);
                        r.Arc = ArcOfLine(r, world.Objects[o].Line);
                        moved = r.Edge != n.Edge;
                    }
                    break;
                }
                default:
                    // Free / Shoreline（海岸折线没有签名，抓取的顺序也不稳）⇒ 直接走下面的「按位置重新吸附」。
                    break;
            }

            if (resolved)
            {
                if (moved && stats != null) stats.Rebound++;
                return KeepPos(r, n.Pos);
            }

            // —— 签名搬不到（或本来就是自由点）：按位置重新认一次。
            //    走 FindSnap 而不是自己写一遍候选收集，是为了和「玩家刚点下那一格」用同一套档位口径
            //    （中心线 vs 路缘、跨越闸、权重），否则重绑出来的锚点会和刚点的那格不一致。
            PlacedNode fresh = SnapKit.FindSnap(n.Pos, world, cfg, tuning, SnapKit.FreeNode(n.Pos), new SnapKit.SnapExtra());
            if (fresh.Kind == SnapKind.Free)
            {
                if (stats != null && n.Kind != SnapKind.Free) stats.Lost++;
                PlacedNode free = SnapKit.FreeNode(n.Pos);
                return free;
            }
            if (stats != null) stats.Resnapped++;
            fresh.Pos = n.Pos;                       // 位置永远是玩家那一格
            fresh.Arc = ArcOfFresh(fresh, world);    // 弧长按「原位置投影到它新认的那条线」重量
            return fresh;
        }

        /// <summary>
        /// 给还没锚点的一格（开局接管 / 自愈收进来的那些格）补一个锚点：
        /// 只补「挂在哪条线的哪个弧长」，位置一个毫米都不动。
        /// 认不出来就保持自由点 —— 那一条边走直线，比硬吸到别处去安全。
        /// </summary>
        public static PlacedNode Attribute(PlacedNode n, WorldSnapshot world, ModConfig cfg, ResolvedTuning tuning)
        {
            return Node(n, world, cfg, tuning, null);
        }

        /// <summary>重绑之后逐条边作废由调用方负责（ManualStack.MarkAllStale）；这里只管锚点本身。</summary>
        public static int Stack(ManualStack stack, WorldSnapshot world, ModConfig cfg, ResolvedTuning tuning, Stats stats)
        {
            if (stack == null || world == null) return 0;
            for (int i = 0; i < stack.Nodes.Count; i++)
            {
                PlacedNode old = stack.Nodes[i];
                PlacedNode neu = Node(old, world, cfg, tuning, stats);
                if (!SameAnchor(old, neu)) stack.Nodes[i] = neu;
            }
            return 0;
        }

        private static bool SameAnchor(PlacedNode a, PlacedNode b)
        {
            return a.Kind == b.Kind && a.Edge == b.Edge && a.GraphNode == b.GraphNode
                   && Math.Abs(a.Arc - b.Arc) < 1e-9 && a.Side == b.Side
                   && a.Pos.DistanceTo(b.Pos) <= GeoKit.EPS;
        }

        private static PlacedNode KeepPos(PlacedNode r, P3 pos)
        {
            r.Pos = pos;
            return r;
        }

        /// <summary>锚点在它自己那条边（按侧取折线）上的弧长位置。</summary>
        private static double ArcOfPosition(PlacedNode n, GraphEdge ge)
        {
            if (ge == null) return n.Arc;
            return ArcOfLine(n, ge.LineFor(n.Side));
        }

        /// <summary>重绑失败、改认了新对象之后，把弧长按新线重新量一遍（Pos 是玩家那一格）。</summary>
        private static double ArcOfFresh(PlacedNode n, WorldSnapshot world)
        {
            if (!AnchorCode.IsNet(n.Edge))
            {
                if (AnchorCode.IsBorder(n.Edge))
                {
                    int b = AnchorCode.Decode(n.Edge, AnchorCode.BORDER_BASE);
                    return b >= 0 && b < world.Borders.Count ? ArcOfLine(n, world.Borders[b].Line) : 0;
                }
                if (AnchorCode.IsObject(n.Edge))
                {
                    int o = AnchorCode.Decode(n.Edge, AnchorCode.OBJECT_BASE);
                    return o >= 0 && o < world.Objects.Count ? ArcOfLine(n, world.Objects[o].Line) : 0;
                }
                if (AnchorCode.IsTile(n.Edge))
                {
                    int t = AnchorCode.Decode(n.Edge, AnchorCode.TILE_BASE);
                    return t >= 0 && t < world.MapTiles.Count ? ArcOfLine(n, world.MapTiles[t].Line) : 0;
                }
                return 0;
            }
            if (n.Edge < 0 || n.Edge >= world.Edges.Count) return 0;
            GraphEdge ge = world.Edges[n.Edge];
            if (ge == null) return 0;
            if (n.Kind == SnapKind.NetNode)
            {
                // 交叉口：弧长就是「这条边的哪一头」，按新下标重认一次端点，别按位置量（量出来是 3 米误差）。
                return SnapKit.ArcOfNode(world, n.Edge, n.GraphNode);
            }
            return ArcOfLine(n, ge.LineFor(n.Side));
        }

        /// <summary>把「玩家那一格的位置」投影到折线上，取它的弧长。折线不可用时保留原弧长。</summary>
        private static double ArcOfLine(PlacedNode n, Polyline line)
        {
            if (line == null || line.Count < 2) return n.Arc;
            P3 hit;
            int segIdx;
            double segT;
            double arc;
            GeoKit.ClosestPointOnPolylineProbe(line, n.Pos, out hit, out segIdx, out segT, out arc);
            return arc;
        }
    }
}
