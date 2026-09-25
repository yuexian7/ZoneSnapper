using System;
using System.Reflection;
using Game.Tools;
using HarmonyLib;
using Unity.Entities;
using Unity.Jobs;
using ZoneSnapper.GameSide;

namespace ZoneSnapper.Patches
{
    /// <summary>
    /// 对 AreaToolSystem 的两个 private 方法下 Harmony 补丁 —— 本模组唯一的补丁点。
    ///
    /// 【为什么只有这两处】
    /// AreaToolSystem 的公开面（FACT：反编译 ATS:2585-3223）只有 GetControlPoints / GetPrefab /
    /// TrySetPrefab / state / mode / actualMode / recreate / GetAvailableSnapMask 等，
    /// 没有任何「加一个控制点」或「玩家刚点了左键」的公开入口。控制点表本身公开可写，
    /// 所以投影、描边、撤点重建、变化重算全部走数据层（见 Systems/ZoneSnapperSystem.cs），
    /// 补丁只承担一件事：把「玩家刚提交了节点 / 刚撤销了节点」这两个事件告诉我们。
    ///
    /// 【为什么不 patch 判定逻辑】
    /// 游戏提交新节点时会校验「倒数第二格 ↔ 最后一格」距离（FACT：ATS:3509-3511 +
    /// AreaUtils.GetMinNodeDistance = m_SnapDistance*0.5）。本模组的投影形状让倒数第二格恒为
    /// 「最后一个手动节点」，所以那道校验的原语义完全保留，不需要动它一根手指。
    ///
    /// 【补丁目标方法与证据】
    ///   private JobHandle Apply(JobHandle inputDeps, bool singleFrameOnly = false)   ATS:3408
    ///   private JobHandle Cancel(JobHandle inputDeps, bool singleFrameOnly = false)   ATS:3283
    /// 两者都在 OnUpdate 里由 applyAction / secondaryApplyAction 的 WasPressedThisFrame 分派
    /// （FACT：ATS:3151-3157），且都不是 [BurstCompile]（Burst 只出现在 ATS:63/1296/1347 的嵌套 job 结构上）
    /// ⇒ 可以正常打补丁。
    /// </summary>
    public static class AreaToolPatches
    {
        /// <summary>本帧是否发生了「玩家左键提交了一个节点」。</summary>
        private static int s_commit;

        /// <summary>本帧是否发生了「玩家右键撤销」。</summary>
        private static int s_pop;

        /// <summary>提交之后工具的 state：用来区分「落了个点」和「闭合创建了区域」。</summary>
        private static volatile string s_afterCommitState = "";

        public static void MarkCommit() { System.Threading.Interlocked.Exchange(ref s_commit, 1); }
        public static void MarkPop() { System.Threading.Interlocked.Exchange(ref s_pop, 1); }

        /// <summary>取走并清零事件标志（一次读一次清，避免上一条残留到下一条 —— Playbook §3.5 的同类坑）。</summary>
        public static bool TakeCommit() { return System.Threading.Interlocked.Exchange(ref s_commit, 0) == 1; }
        public static bool TakePop() { return System.Threading.Interlocked.Exchange(ref s_pop, 0) == 1; }
        public static string TakeAfterCommitState()
        {
            string s = s_afterCommitState;
            s_afterCommitState = "";
            return s;
        }

        public static void ApplyPostfix(AreaToolSystem __instance)
        {
            // 补丁体必须极窄且吞掉一切异常：这里出问题最坏结果是「这一次没插描边点」，
            // 绝不能让玩家画不出区域。
            try
            {
                MarkCommit();
                if (__instance != null) s_afterCommitState = __instance.state.ToString();
            }
            catch (Exception) { }
        }

        public static void CancelPostfix(AreaToolSystem __instance)
        {
            try
            {
                MarkPop();
            }
            catch (Exception) { }
        }

        private const string KApply = "Apply";
        private const string KCancel = "Cancel";

        private static MethodBase s_applyOriginal;
        private static MethodBase s_cancelOriginal;
        private static MethodInfo s_applyPostfix;
        private static MethodInfo s_cancelPostfix;

        /// <summary>
        /// 安装补丁。返回已成功打上的补丁数（0 表示游戏改了方法名/签名 —— 必须打进日志，
        /// 这是实机 T4 的哨兵之一：模组没反应时第一个要看的数字）。
        /// </summary>
        public static int Install(Harmony harmony, out string error)
        {
            error = null;
            int ok = 0;
            try
            {
                Type t = typeof(AreaToolSystem);
                MethodInfo apply = AccessTools.Method(t, KApply, new Type[] { typeof(JobHandle), typeof(bool) });
                MethodInfo cancel = AccessTools.Method(t, KCancel, new Type[] { typeof(JobHandle), typeof(bool) });

                s_applyPostfix = typeof(AreaToolPatches).GetMethod("ApplyPostfix", BindingFlags.Public | BindingFlags.Static);
                s_cancelPostfix = typeof(AreaToolPatches).GetMethod("CancelPostfix", BindingFlags.Public | BindingFlags.Static);

                if (apply != null)
                {
                    harmony.Patch(apply, null, new HarmonyMethod(s_applyPostfix));
                    s_applyOriginal = apply;
                    ok++;
                }
                if (cancel != null)
                {
                    harmony.Patch(cancel, null, new HarmonyMethod(s_cancelPostfix));
                    s_cancelOriginal = cancel;
                    ok++;
                }
                if (apply == null || cancel == null)
                {
                    error = "AreaToolSystem 的 Apply/Cancel 签名与预期不符（游戏版本变动？）：apply=" +
                            (apply != null ? "ok" : "missing") + " cancel=" + (cancel != null ? "ok" : "missing");
                }
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
            }
            return ok;
        }

        /// <summary>
        /// 逐个精确拆补丁。
        /// 为什么不用 UnpatchSelf / UnpatchAll：编译期引用的 0Harmony 2.2.2 里 Harmony 类只有
        /// UnpatchAll(string harmonyID = null)（FACT：ilspycmd -t HarmonyLib.Harmony 对 2.2.2 的输出），
        /// 而运行时被换成 2.3.3 那份上 UnpatchAll(string) 是 obsolete-as-error；
        /// 拆自己那两个补丁本来就不需要按 id 全拆 —— 精确 Unpatch(original, patch) 两份 API 都有，
        /// 也绝不会碰到别的模组的补丁。
        /// </summary>
        public static void Uninstall(Harmony harmony)
        {
            if (harmony == null) return;
            try { if (s_applyOriginal != null && s_applyPostfix != null) harmony.Unpatch(s_applyOriginal, s_applyPostfix); }
            catch (Exception e) { SnapperLog.Warn("拆 Apply 补丁失败：" + e.GetType().Name); }
            try { if (s_cancelOriginal != null && s_cancelPostfix != null) harmony.Unpatch(s_cancelOriginal, s_cancelPostfix); }
            catch (Exception e) { SnapperLog.Warn("拆 Cancel 补丁失败：" + e.GetType().Name); }
            s_applyOriginal = null;
            s_cancelOriginal = null;
        }
    }
}
