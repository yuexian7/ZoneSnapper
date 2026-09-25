using System;
using System.Collections.Generic;

namespace ZoneSnapper.Engine
{
    /// <summary>
    /// 投影层：本模组的核心不变量所在。
    ///
    /// 【为什么需要这一层】
    /// 游戏把正在画的区域存在 AreaToolSystem 的一个 NativeList&lt;ControlPoint&gt; 里
    /// （FACT：ATS:2557，公开读写口 ATS:2973），并且有两条硬约定：
    ///   ① 最后一格永远是跟随鼠标的游标（FACT：ATS:3678 math.select(0, Length-1, state==Create)）；
    ///   ② 提交新节点时校验「倒数第二格 ↔ 最后一格」的距离 &gt;= m_SnapDistance*0.5
    ///      （FACT：ATS:3509-3511 + AreaUtils.GetMinNodeDistance）。
    /// 于是描边点只能插在「它所指向的那个手动节点之前」，形状恒为：
    ///   [m0, T(m0→m1), m1, T(m1→m2), m2, …, mN, cursor]
    /// 这样 ① 仍然成立，② 检查的仍然是「上一个手动节点 → 光标」，游戏的校验一个字都不用改，
    /// 也不需要 patch 掉任何判定。
    ///
    /// 【需求 3 为什么自动成立】
    /// 手动栈（manual[]）只装玩家真正点过的节点；描边点永远是它的派生物。
    /// 游戏右键弹掉一格 = 弹掉最后一个手动节点（FACT：ATS:3330 删游标 + ATS:3345 把新射线点写回末格），
    /// 我们随后按弹掉后的栈重建投影，那条边的描边点整批消失，绝不会「右键删掉一个自动点」。
    ///
    /// 【写表的四道闸】本文件里有四个纯函数判据，系统在动那张表之前必须逐个过：
    ///   <see cref="GameAcceptedCommit"/> 这一击游戏到底落点了没有（事件 ≠ 落点）
    ///   <see cref="ShouldAdopt"/>      开局接管游戏已有的控制点（编辑/重建路径）
    ///   <see cref="MissingTail"/>      漏收事件后的自愈尾巴
    ///   <see cref="MayWrite"/>         总闸：要减点时必须先证明「表里这些格全是我们自己写的」
    /// 口径：<b>半死不活比完全不工作有害得多</b> —— 任何一道闸不过，这一帧的动作是「什么都不写」，
    /// 玩家拿到的是原版手感 + 一行可判读的日志，而不是一个被改坏的区域工具。
    /// </summary>
    public sealed class ManualStack
    {
        public readonly List<PlacedNode> Nodes = new List<PlacedNode>();
        /// <summary>edges[i] = Nodes[i] → Nodes[i+1] 的描边结果。数量恒为 Nodes.Count-1（可能为 null 表示还没算）。</summary>
        public readonly List<TraceResult> Edges = new List<TraceResult>();
        /// <summary>整栈签名，用于「什么都没变就别动游戏的表」（Playbook：不要和游戏抢写同一份数据）。</summary>
        public long Signature;

        public int Count { get { return Nodes.Count; } }

        public void Clear()
        {
            Nodes.Clear();
            Edges.Clear();
            Signature = 0;
        }

        public void Push(PlacedNode p)
        {
            Nodes.Add(p);
            if (Nodes.Count >= 2) Edges.Add(null);
            Touch();
        }

        /// <summary>弹掉最后一个手动节点（需求 3）。返回是否真的弹掉了。</summary>
        public bool PopLast()
        {
            if (Nodes.Count == 0) return false;
            Nodes.RemoveAt(Nodes.Count - 1);
            // 弹掉最后一个节点后，指向它的那条边也作废。
            // 判据必须是「还有边可弹」：只剩 1 个节点时 Edges.Count==0 && Nodes.Count==0，
            // 老写法 0>=0 成立会 RemoveAt(-1) 直接抛（回归壳 R 段抓到，实机就是在 ToolUpdate 里炸）。
            if (Edges.Count > 0 && Edges.Count >= Nodes.Count) Edges.RemoveAt(Edges.Count - 1);
            Touch();
            return true;
        }

        public void SetEdge(int i, TraceResult r)
        {
            if (i < 0 || i >= Edges.Count) return;
            Edges[i] = r;
            Touch();
        }

        /// <summary>某条边的描边结果还能不能用：算它时用的世界签名与当前一致 ⇒ 复用（需求 5 的增量大算）。</summary>
        public bool EdgeIsFresh(int i, long worldSignature)
        {
            if (i < 0 || i >= Edges.Count) return false;
            TraceResult r = Edges[i];
            return r != null && r.WorldSignature != 0 && r.WorldSignature == worldSignature;
        }

        /// <summary>记下「这条边是在哪个世界签名下算出来的」。</summary>
        public void StampEdge(int i, long worldSignature)
        {
            if (i < 0 || i >= Edges.Count) return;
            if (Edges[i] != null) Edges[i].WorldSignature = worldSignature;
        }

        /// <summary>
        /// 让所有缓存的描边作废（需求 5：路网脏了）。
        /// 作废的是 <c>WorldSignature</c> —— EdgeIsFresh 读的就是它。
        /// 老实现清的是 Signature（那条边自己用到的边签名），清完 EdgeIsFresh 照样返回 true，
        /// 于是「改了路」永远不会重算（回归壳 T7 抓到，等于需求 5 白做）。
        /// </summary>
        public void MarkAllStale()
        {
            for (int i = 0; i < Edges.Count; i++)
            {
                if (Edges[i] != null) Edges[i].WorldSignature = 0;
            }
        }

        private void Touch()
        {
            unchecked
            {
                long h = 1469598103934665603L;
                for (int i = 0; i < Nodes.Count; i++)
                {
                    h = (h ^ (long)Nodes[i].Pos.X) * 1099511628211L;
                    h = (h ^ (long)Nodes[i].Pos.Y) * 1099511628211L;
                    h = (h ^ (long)Nodes[i].Kind) * 1099511628211L;
                }
                Signature = h;
            }
        }
    }

    /// <summary>投影里的一个槽位。IsManual 供诊断与标记绘制使用。</summary>
    public struct Slot
    {
        public P3 Pos;
        public bool IsManual;
        /// <summary>自动点归属于哪个手动节点（该边终点）；手动点为自身下标。</summary>
        public int OwnerEdge;
    }

    /// <summary>一次投影的结果。Cursor 单独存，因为它由游戏维护。</summary>
    public sealed class Projection
    {
        public readonly List<Slot> Slots = new List<Slot>();
        public int ManualCount;
        public int AutoCount;
        public bool Truncated;

        /// <summary>投影后（不含光标）的位置序列。</summary>
        public List<P3> Positions()
        {
            List<P3> l = new List<P3>(Slots.Count);
            for (int i = 0; i < Slots.Count; i++) l.Add(Slots[i].Pos);
            return l;
        }
    }

    public static class ProjectKit
    {
        /// <summary>
        /// 由手动栈 + 各边描边结果生成控制点序列。纯函数：同样的输入必然同样的输出，
        /// 所以「道路改了 ⇒ 重算」和「右键撤销 ⇒ 重算」是同一条代码路径，不存在两种行为。
        /// </summary>
        public static Projection Build(ManualStack stack, ModConfig cfg, ResolvedTuning tuning)
        {
            Projection p = new Projection();
            if (stack == null || stack.Count == 0) return p;
            if (cfg == null) cfg = ModConfig.CreateDefault();

            p.ManualCount = stack.Count;
            p.Slots.Add(new Slot { Pos = stack.Nodes[0].Pos, IsManual = true, OwnerEdge = -1 });
            for (int i = 1; i < stack.Count; i++)
            {
                List<P3> mid = MiddlePoints(stack, i, cfg, tuning, p);
                for (int k = 0; k < mid.Count; k++)
                {
                    p.Slots.Add(new Slot { Pos = mid[k], IsManual = false, OwnerEdge = i - 1 });
                    p.AutoCount++;
                }
                p.Slots.Add(new Slot { Pos = stack.Nodes[i].Pos, IsManual = true, OwnerEdge = -1 });
            }
            return p;
        }

        /// <summary>
        /// 第 i-1→i 条边的中间点。没有点就是**手动路径**（反馈 4 的定义：两端都是手动点、中间没有
        /// 自动放置的节点 ⇒ 首尾直接连直线）。
        ///
        /// 这里原来还有一条「两端都自由时补一段弧」（CurveFreeEdges/CurveSegments/CurveBulge），
        /// 第五轮反馈 7 明确不要它，改成了 <see cref="TraceKit"/> 里的**节点续接**：
        /// 能贴网络的那一段照样贴，剩下的用直线连。弧线是在凭空造形状，续接是在还原玩家想要的那条路。
        /// </summary>
        private static List<P3> MiddlePoints(ManualStack stack, int i, ModConfig cfg, ResolvedTuning tuning, Projection p)
        {
            TraceResult tr = (stack.Edges != null && i - 1 < stack.Edges.Count) ? stack.Edges[i - 1] : null;
            if (tr == null || tr.Points == null || tr.Points.Count == 0) return new List<P3>();
            List<P3> pts = tr.Points;
            int cap = tuning.MaxNodesPerEdge > 0 ? tuning.MaxNodesPerEdge : ModConfig.HARD_NODE_CAP;
            if (pts.Count > cap)
            {
                pts = pts.GetRange(0, cap);
                p.Truncated = true;
            }
            return pts;
        }

        /// <summary>
        /// 闭合环：最后一条边（mN→m0）也描一遍，产出真正写进 Game.Areas.Node 的那圈点。
        /// 为什么闭合边不进实时投影：进了就会占掉倒数第二格，破坏游戏的最小间距校验（见本文件头）。
        /// </summary>
        public static List<P3> BuildClosedRing(ManualStack stack, TraceResult closingEdge, ModConfig cfg, ResolvedTuning tuning)
        {
            Projection p = Build(stack, cfg, tuning);
            List<P3> ring = p.Positions();
            if (closingEdge != null && closingEdge.Points != null)
            {
                for (int i = 0; i < closingEdge.Points.Count; i++) ring.Add(closingEdge.Points[i]);
            }
            // 去掉与首点重合的尾点：游戏侧的闭合方式是「首尾各存一次」还是「环形索引」由调用方决定，
            // 这里保持「不重复首点」的约定，交给 GameSide 复制首点到尾部（FACT：AreaToolSystem 生成
            // 地图瓦片时是 dynamicBuffer[4] = dynamicBuffer[0]，即闭合环要重复首点 —— ATS:1499）。
            while (ring.Count > 1 && ring[ring.Count - 1].DistanceTo(ring[0]) <= tuning.MinSpacing)
            {
                ring.RemoveAt(ring.Count - 1);
            }
            return ring;
        }

        /// <summary>
        /// 开局「接管」判定：我们手上一个手动节点都没有，而游戏的表里已经躺着控制点了。
        ///
        /// 两种真实来源（都不是 bug）：
        ///  ① **编辑/重建已有区域**：放完填埋场/产业建筑后，游戏自己把 `AreaToolSystem.recreate` 设上、
        ///     `mode=Edit`，并把该区域**现有的**一圈节点灌进 m_ControlPoints 让你接着改
        ///     （FACT：Game.Tools/ObjectToolSystem.cs:3992-3998 设 recreate+Edit+activeTool；
        ///       信息面板那条路是 Game.UI.InGame/ActionsSection.cs:221-233 直接把 Lot prefab 交给 AreaToolSystem）。
        ///     v0.1.0 在系统入口写了 `recreate != Entity.Null ⇒ 直接返回`，等于把这类绘制整个排除在贴合之外 ——
        ///     玩家看到的就是「填埋区域能正常画，但模组完全没参与」。
        ///  ② 提交事件漏收（补丁只打上一个、或别的模组先动了表）：这时接管比「每帧拒写」有用。
        ///
        /// 接管一律**不重新吸附**（照原位置收进栈），因为①里那些点是玩家已经存好的几何，
        /// 我们擅自挪动它就是改存档；贴合只该作用在玩家接下来新点的那些点上。
        /// </summary>
        public static bool ShouldAdopt(int manualCount, int gameCountWithoutCursor)
        {
            return manualCount == 0 && gameCountWithoutCursor > 0;
        }

        /// <summary>
        /// 「这一帧到底能不能动游戏那张控制点表」的总闸（纯函数，回归壳 P2b 钉死）。
        ///
        /// 实机 v0.1.0 的事故：ZoneSnapperApplySystem.OnCreate 抛异常 ⇒ OnLoad 中断 ⇒ Harmony 补丁一步没走到
        /// ⇒ 手动栈永远是空，而主系统照样每帧把「空投影」写进 AreaToolSystem 的表 ——
        /// 玩家刚点下的第一个节点被抹掉，表现是「区域工具点了没反应」（日志证据：manual=0 恒定、
        /// [Hook] 那行根本不存在、统计行照刷）。
        ///
        /// 【第一版闸：只加不减 —— 太狠，会把需求 3 弄坏】右键撤销的净效果是
        /// 「弹掉一个手动节点 **+ 那条边的全部描边点**」（FACT：ATS:3283-3345，Cancel 只 RemoveAtSwapBack 掉游标那一格），
        /// 也就是**必须减点**；把滑杆往「更简化」方向调同理。一刀切禁减的结果是右键删掉的那个角还留在表里，
        /// 而每一帧都撞闸 ⇒ desync 疯涨 ⇒ 自愈把游戏剩下的描边点当成「玩家新点的节点」收进栈 ⇒ 环彻底乱掉。
        ///
        /// 【现在的判据：减点只允许减在我们自己写进去的那些点上】
        ///  ① 我们不比表里少 ⇒ 直接放行（这条保住 v0.1.0 那个事故：0 槽 vs 表里有玩家的点，玩家的点不在
        ///     我们写过的任何一帧里 ⇒ 走 ② 被拒）。
        ///  ② 要减点 ⇒ 表里**每一格**都必须能在「我们上一帧真正写进表的那份投影」里找到同位置点（容差 eps）。
        ///     全都对得上 ⇒ 这张表就是我们自己铺的，减多少都不会碰到玩家的东西；
        ///     有任何一格对不上 ⇒ 那是玩家点的、或别的模组/游戏自己灌进来的（编辑重建、表被清过），不许减。
        /// eps 由调用方给宽一点（游戏在 Apply 之后还会跑 SnapControlPoints 挪点，见 ZoneSnapperSystem 的调用处）。
        /// </summary>
        public static bool MayWrite(Projection p, IList<P3> currentWithoutCursor, IList<P3> lastWritten, double eps)
        {
            if (p == null) return false;
            if (currentWithoutCursor == null) return true;
            if (p.Slots.Count >= currentWithoutCursor.Count) return true;

            // 要减点：表里必须整张都出自我们上一帧写的那份
            if (lastWritten == null || lastWritten.Count == 0) return false;
            for (int i = 0; i < currentWithoutCursor.Count; i++)
            {
                if (!NearAny(currentWithoutCursor[i], lastWritten, eps)) return false;
            }
            return true;
        }

        /// <summary>q 是否落在 pts 里某点的 eps 邻域内（线性扫：两边都不超过几十个点）。</summary>
        private static bool NearAny(P3 q, IList<P3> pts, double eps)
        {
            for (int i = 0; i < pts.Count; i++)
            {
                if (pts[i].DistanceTo(q) <= eps) return true;
            }
            return false;
        }

        /// <summary>一次自愈最多收下几个点（再多就不像漏事件，更像我们对表的理解错了）。</summary>
        public const int kMaxRecoverPerStep = 8;

        /// <summary>
        /// 「游戏这一击到底落点了没有」——收点前必须过的第二道闸（纯函数，回归壳 P2d 钉死）。
        ///
        /// 【为什么事件本身不够】AreaToolSystem.Apply 的 State.Create 分支在把游标格追加成新节点**之前**
        /// 有一道最小间距校验：
        ///   FACT：ATS:3509-3511 —— <c>num = distance(cps[Len-2], cps[Len-1]); if (num &gt;= minNodeDistance2) {{...加点...}}</c>，
        ///         这个 if **没有 else**，距离不够时整段跳过，直接落到 ATS:3567 的 <c>return Update(...)</c>：
        ///         一个节点都不加、表长不变。而我们的 Harmony 后缀是方法正常返回就跑的。
        /// 【不拦的后果】District 的门槛是 <c>m_SnapDistance*0.5 = 32</c> 米（FACT：ATS:3458 +
        ///   Game.Areas.AreaUtils.GetMinNodeDistance 类别表 Lot 8 / District 32 / MapTile 64 / Space 1 / Surface 0.75），
        ///   玩家挨着点两下是再正常不过的操作 ⇒ 我们会把上一个手动节点重复收进栈：环里出现自重叠点、
        ///   那条边退化，右键撤销还得点两下才掉一个看得见的节点（直接违背需求 3）。
        /// 【判据】我们上一帧写进表的槽位数 &lt; 现在表里的格数 ⇒ 游戏确实自己加了一格，这一击算落点。
        /// 开局那一击（栈还是空）例外：那是 State.Default → State.Create 的转换，
        /// 游戏在 ATS:3478-3486 先把点清成 [value] 再补游标，必然落了一格。
        /// </summary>
        public static bool GameAcceptedCommit(int manualCount, int lastWrittenSlotCount, int currentCountWithoutCursor)
        {
            if (manualCount == 0) return true;
            if (lastWrittenSlotCount < 0) return true;      // 还没写过任何一帧，没有可比对的基线
            return currentCountWithoutCursor >= lastWrittenSlotCount + 1;
        }

        /// <summary>
        /// 被 <see cref="MayWrite"/> 连续挡下若干帧后的自愈候选：表里比我们投影多出来的那几格。
        ///
        /// 【为什么这一头一定是玩家点的】我们的投影就是上一帧写进表里的内容。表比它长出来的部分
        /// 只能发生在尾巴上（我们从不减点），而尾巴上多出来的只可能是游戏自己落定的那一格 ——
        /// 也就是我们漏收的一次左键提交（补丁只打上一个、或别的模组在同一帧动了表）。
        ///
        /// 【为什么要去掉重合的】游戏点闭合时会把游标格（= 起点 m0）原样追加成一格。
        /// 那格不是新节点，收下它就等于环里多一个自重叠点，后面整条边的描边都会歪。
        ///
        /// 收进栈时一律 <c>SnapKind.Free</c>、不重新吸附：这些点是玩家自己放的位置。
        /// </summary>
        public static List<P3> MissingTail(Projection p, IList<P3> currentWithoutCursor, ManualStack stack, double eps)
        {
            List<P3> tail = new List<P3>();
            if (p == null || currentWithoutCursor == null) return tail;
            int extra = currentWithoutCursor.Count - p.Slots.Count;
            if (extra <= 0) return tail;
            if (extra > kMaxRecoverPerStep) extra = kMaxRecoverPerStep;
            for (int i = currentWithoutCursor.Count - extra; i < currentWithoutCursor.Count; i++)
            {
                P3 q = currentWithoutCursor[i];
                bool dup = false;
                if (stack != null)
                {
                    for (int j = 0; j < stack.Nodes.Count; j++)
                    {
                        if (stack.Nodes[j].Pos.DistanceTo(q) <= eps) { dup = true; break; }
                    }
                }
                if (!dup) tail.Add(q);
            }
            return tail;
        }

        /// <summary>
        /// 「要不要动游戏那张表」的判定：位置逐格比较，容差 1cm。
        /// 每帧无条件重写 = 与游戏抢写同一份 buffer，Playbook 把这条列为最大的性能杀手（v0.7.0/v0.7.1 的教训）。
        /// </summary>
        public static bool NeedsWrite(Projection p, IList<P3> currentWithoutCursor, P3 currentCursor, P3 wantedCursor, double eps)
        {
            if (p == null) return false;
            if (currentWithoutCursor == null || currentWithoutCursor.Count != p.Slots.Count) return true;
            if (currentCursor.DistanceTo(wantedCursor) > eps) return true;
            for (int i = 0; i < p.Slots.Count; i++)
            {
                if (currentWithoutCursor[i].DistanceTo(p.Slots[i].Pos) > eps) return true;
            }
            return false;
        }

        /// <summary>
        /// 是否已经「点回起点」= 游戏判定闭合的那一瞬。
        /// 游戏自己的条件是 Length&gt;=4 且游标被吸到 cp0（FACT：ATS:811-818），
        /// 我们用同样的判据（位置重合 + 至少 3 个手动节点），保证与游戏的提交时机不错位。
        /// </summary>
        public static bool IsClosing(ManualStack stack, P3 cursor, double eps)
        {
            if (stack == null || stack.Count < 3) return false;
            return cursor.DistanceTo(stack.Nodes[0].Pos) <= eps;
        }
    }
}
