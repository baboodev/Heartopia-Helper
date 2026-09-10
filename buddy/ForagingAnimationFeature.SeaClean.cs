using System;
using System.Collections.Generic;
using UnityEngine;

namespace HeartopiaMod
{
    // ============================================================================================
    // Foraging animations, sea-clean half — the cleaning pose while Aura Farm sweeps pollutants.
    //
    // Same promise as the land half (ForagingAnimationFeature.cs): with a player close enough to
    // see it, the character does the thing it is doing. Aura Farm's sweep kills pollutants with a
    // direct ExecuteKill, so a witness sees them pop with nobody moving.
    //
    // ── WHY THIS CANNOT BE A CAST LIKE THE SWINGS ───────────────────────────────────────────────
    // The chop/mine swings are ActorBehaveClips: one cast is one whole swing, it replicates through
    // CastActionEvent, and it ends by itself. Sea cleaning is neither of those things. It rides two
    // independent channels:
    //
    //   * WHICH CLIP is a MOTION. PlayerSeaCleanMotionArg (ActionId.PlayerSeaCleanMotion = 663) is
    //     handed to LocalPlayerComponent.SetMotion(CharacterPart.Body, ctx). Because its clip
    //     derives from ActorMotionClip, PlayerSyncStatus.SendCastEvent routes it into
    //     FsmStatus.MotionData instead of CastActionEvent (and only ever with ActionPhase.Start),
    //     and a remote client rebuilds the same motion from that field.
    //   * WHICH PHASE is a separate synced field. PlayerStateSeaClean.SetAnimPhase writes
    //     SeaCleanStatus.AnimPhase (NetId 59/309). Nothing "executes" it on the far side:
    //     PlayerSeaCleanMotion.OnMotionTick POLLS it every frame and fires the animator triggers
    //     (StartClean / QteStart / QteEnd / QteExit / StopClean) plus the tool VFX.
    //
    // So a lone cast animates nobody and a local PlayController animates nobody but us. Both
    // channels have to be driven — and, unlike a swing, both have to be taken back DOWN again: a
    // sweep has no natural end, and a character left in phase Cleaning keeps scrubbing at nothing.
    //
    // ── WHAT IT DOES NOT ALLOCATE ───────────────────────────────────────────────────────────────
    // The motion context is NOT created here. PlayerStateBase caches OnStateInit()'s result in
    // _motionContext, so the FSM's own PlayerStateSeaClean already holds exactly the object the
    // game itself would pass, and we borrow it through get_motionContext.
    //
    // ── GATES ───────────────────────────────────────────────────────────────────────────────────
    // Swimming (the clip is a PlayerSwimMotion — on land it reads as the sea-cleaner land-pose bug),
    // the cleaner actually in hand (equipping AFTER the motion starts would re-apply the handhold's
    // own pose controller over ours), and a witness within the same radius the land half uses.
    //
    // Decoration only: the sweep in RunContaminationCleanWait is untouched, the animation never
    // gates a kill, and nothing reaches the server beyond the two status fields the game syncs
    // for its own cleaning.
    // ============================================================================================
    public partial class HeartopiaComplete
    {
        // How often the witness scan and the motion-still-ours check run during a dwell. The dwell
        // ticks every frame; neither check is worth doing at that rate.
        private const float ForagingAnimSeaCheckInterval = 1f;

        private const float ForagingAnimSeaResolveRetry = 2f;

        // PlayerState.SeaClean — the FSM slot whose state object owns the motion context and the
        // SetAnimPhase we drive.
        private const int ForagingAnimSeaPlayerStateId = 59;

        private const int ForagingAnimSeaCharacterPartBody = 0;

        // SeaCleanAnimPhase : byte. Cleaning is the looping scrub; None is what the game leaves
        // behind when it exits the state.
        private const byte ForagingAnimSeaPhaseNone = 0;
        private const byte ForagingAnimSeaPhaseCleaning = 2;

        // How stale the remembered target may be before the animation stops claiming a type. The
        // sweep re-scans twice a second, so anything older than this means it found nothing.
        private const float ForagingAnimSeaTargetFreshSeconds = 5f;

        private bool foragingAnimSeaActive;      // we drove the motion + phase on
        private bool foragingAnimSeaWitnessed;   // hysteresis, kept apart from the land half's
        private float foragingAnimSeaNextCheckAt;
        private float foragingAnimSeaResolveRetryAt;

        // The one thing about the TARGET that reaches a watcher: SeaCleanStatus.TargetTypeId picks
        // the debris VFX and sound (SeaCleanConfig.GetPollutionType -> cleanToolAdditionalVfxId).
        // Position and netId are NOT in the status at all, and the aim IK is local-only, so this is
        // the whole of "what am I cleaning" as far as anyone else is concerned.
        private int foragingAnimSeaTargetTypeId;
        private float foragingAnimSeaTargetSeenAt = -999f;
        private IntPtr foragingAnimSeaTypeIdClass;
        private IntPtr foragingAnimSeaGetTypeIdMethod;

        private IntPtr foragingAnimSeaPlayerClass;
        private IntPtr foragingAnimSeaMotionArgClass;
        private IntPtr foragingAnimSeaGetStateMethod;
        private IntPtr foragingAnimSeaGetStateIdMethod;
        private IntPtr foragingAnimSeaGetMotionCtxMethod;
        private IntPtr foragingAnimSeaSetAnimPhaseMethod;
        private IntPtr foragingAnimSeaSetTargetTypeMethod;
        private IntPtr foragingAnimSeaSetMotionMethod;
        private IntPtr foragingAnimSeaGetCurrentStateMethod;
        private IntPtr foragingAnimSeaGetActionGraphMethod;
        private IntPtr foragingAnimSeaForceSyncMethod;

        // ── entry points ────────────────────────────────────────────────────────────────────────

        // Called every frame from RunContaminationCleanWait, after the tool gate has run. Owns the
        // whole decision: start, hold, or stop.
        internal void TickForagingAnimSeaClean(Vector3 node, bool toolReady)
        {
            if (!this.foragingAnimEnabled || !this.autoFarmActive)
            {
                this.StopForagingAnimSeaClean("disabled");
                return;
            }

            float now = Time.unscaledTime;
            if (now < this.foragingAnimSeaNextCheckAt)
            {
                return;
            }

            this.foragingAnimSeaNextCheckAt = now + ForagingAnimSeaCheckInterval;

            // The cleaner has to be in hand BEFORE the motion starts. EquipHandhold.ApplyController
            // Override plays (swim, seacleaner, pose) on equip, which would land on top of the
            // (swim, seacleaner, cleantool) our motion asks for and leave the character posing
            // instead of scrubbing.
            if (!toolReady)
            {
                this.StopForagingAnimSeaClean("no cleaner in hand");
                return;
            }

            // PlayerSeaCleanMotion derives from PlayerSwimMotion. Forcing it while standing gives
            // exactly the land-pose breakage HandHoldSeaCleaner already has, so don't.
            if (!this.TryGetFarmWalkSwimLocomotion(out IntPtr _))
            {
                this.StopForagingAnimSeaClean("not swimming");
                return;
            }

            if (!this.IsForagingAnimWitnessed(node, ref this.foragingAnimSeaWitnessed))
            {
                this.StopForagingAnimSeaClean("nobody watching");
                return;
            }

            // Already running: re-assert the phase (the setter is a no-op when it matches, so this
            // costs nothing and repairs a phase something else reset) and put the motion back if
            // the FSM has since changed state out from under us.
            bool starting = !this.foragingAnimSeaActive;
            if (this.TryDriveForagingAnimSeaClean(ForagingAnimSeaPhaseCleaning, starting
                    ? ForagingAnimSeaMotion.Set
                    : ForagingAnimSeaMotion.RestoreIfLost, out string status))
            {
                if (starting)
                {
                    this.foragingAnimSeaActive = true;
                    this.foragingAnimStatus = "Witnessed — playing the cleaning animation.";
                    if (MasterLogForagingAnim)
                    {
                        ModLogger.Msg("[ForagingAnim] sea clean animation started at " + node);
                    }
                }

                return;
            }

            // A failure while starting is worth a line every time: the whole feature is invisible
            // when it silently does nothing, which is the failure mode the land half already had.
            if (starting)
            {
                this.foragingAnimStatus = "Cleaning animation failed: " + status;
                ModLogger.Msg("[ForagingAnim] sea clean animation failed: " + status);
            }
        }

        // Take the animation back down. Safe to call when it was never started — that is the point,
        // since it is called from every path that ends a dwell or a run.
        internal void StopForagingAnimSeaClean(string reason)
        {
            if (!this.foragingAnimSeaActive)
            {
                return;
            }

            this.foragingAnimSeaActive = false;
            this.foragingAnimSeaWitnessed = false;
            this.foragingAnimSeaNextCheckAt = 0f;

            if (this.TryDriveForagingAnimSeaClean(ForagingAnimSeaPhaseNone,
                    ForagingAnimSeaMotion.Restore, out string status))
            {
                if (MasterLogForagingAnim)
                {
                    ModLogger.Msg("[ForagingAnim] sea clean animation stopped (" + reason + ")");
                }
            }
            else
            {
                // Unconditional: a stop that did not land leaves the character scrubbing at
                // nothing for every witness, which is worse than never having played it.
                ModLogger.Msg("[ForagingAnim] sea clean animation stop FAILED (" + reason + "): " + status);
            }

            this.foragingAnimStatus = "Idle.";
        }

        // Called from the sweep with the pollutant it is about to clean, nearest first. Reading the
        // type here is one invoke on a component the sweep has already pinned; resolving it later,
        // from the animation tick, would mean a second scan of every pollutant in the world.
        internal void NoteForagingAnimSeaTarget(IntPtr monsterComponent)
        {
            if (!this.foragingAnimEnabled || monsterComponent == IntPtr.Zero
                || auraMonoObjectGetClass == null || auraMonoRuntimeInvoke == null)
            {
                return;
            }

            // Resolve on the OBJECT's class, and re-resolve if it ever differs: a method handle
            // looked up by name on one class and invoked against another is a process kill.
            IntPtr klass = auraMonoObjectGetClass(monsterComponent);
            if (klass == IntPtr.Zero)
            {
                return;
            }

            if (klass != this.foragingAnimSeaTypeIdClass)
            {
                this.foragingAnimSeaTypeIdClass = klass;
                this.foragingAnimSeaGetTypeIdMethod =
                    this.FindAuraMonoMethodOnHierarchy(klass, "get_TypeId", 0);
            }

            if (this.foragingAnimSeaGetTypeIdMethod == IntPtr.Zero)
            {
                return;
            }

            IntPtr exc = IntPtr.Zero;
            IntPtr boxed = auraMonoRuntimeInvoke(this.foragingAnimSeaGetTypeIdMethod,
                                                 monsterComponent, IntPtr.Zero, ref exc);
            if (exc != IntPtr.Zero || boxed == IntPtr.Zero
                || !this.TryUnboxMonoInt32(boxed, out int typeId) || typeId == 0)
            {
                return;
            }

            this.foragingAnimSeaTargetTypeId = typeId;
            this.foragingAnimSeaTargetSeenAt = Time.unscaledTime;
        }

        // ── the two channels ────────────────────────────────────────────────────────────────────

        private enum ForagingAnimSeaMotion
        {
            None,            // phase only
            Set,             // hand the body our motion
            RestoreIfLost,   // only if the body motion is no longer ours
            Restore,         // hand the body back to the FSM's current state
        }

        // One pass over the game side: resolve the local player, reach its FSM's SeaClean state,
        // write the phase, and do whatever `motion` asks. Everything happens inside one pin scope —
        // no game pointer outlives this call.
        private unsafe bool TryDriveForagingAnimSeaClean(byte phase, ForagingAnimSeaMotion motion,
                                                         out string status)
        {
            status = "unavailable";
            if (!this.EnsureForagingAnimSeaResolved(out string detail))
            {
                status = detail;
                return false;
            }

            // Fail closed: every handle below is held across a later invoke, and the moving GC
            // relocates an unpinned object the moment anything allocates.
            if (!AuraMonoPinningAvailable)
            {
                status = "pinning unavailable";
                return false;
            }

            List<uint> pins = new List<uint>();
            try
            {
                if (!this.TryAuraMonoGetComponentObjects(this.foragingAnimSeaPlayerClass,
                        out List<IntPtr> players, pins)
                    || players == null || players.Count == 0)
                {
                    status = "no local player";
                    return false;
                }

                IntPtr player = players[0];
                if (!this.TryGetMonoObjectMember(player, "_character", out IntPtr character)
                    || character == IntPtr.Zero)
                {
                    status = "_character unreadable";
                    return false;
                }

                HoldForagingAnimSeaPin(character, pins);

                if (!this.TryGetMonoObjectMember(character, "bodyFsMachine", out IntPtr fsm)
                    || fsm == IntPtr.Zero)
                {
                    status = "bodyFsMachine unreadable";
                    return false;
                }

                HoldForagingAnimSeaPin(fsm, pins);

                IntPtr exc = IntPtr.Zero;
                int stateArg = ForagingAnimSeaPlayerStateId;
                IntPtr* stateArgs = stackalloc IntPtr[1];
                stateArgs[0] = (IntPtr)(&stateArg);
                IntPtr stateObj = auraMonoRuntimeInvoke(this.foragingAnimSeaGetStateMethod, fsm,
                                                        (IntPtr)stateArgs, ref exc);
                if (exc != IntPtr.Zero || stateObj == IntPtr.Zero)
                {
                    status = "GetState(SeaClean) failed";
                    return false;
                }

                HoldForagingAnimSeaPin(stateObj, pins);

                // ⚠️ PlayerFsMachine.GetState returns PlayerStates[0] on a MISS, not null, and
                // SetAnimPhase below was resolved on PlayerStateSeaClean. Invoking it against some
                // other state object is the documented way to fault the process, so verify first.
                IntPtr boxedStateId = auraMonoRuntimeInvoke(this.foragingAnimSeaGetStateIdMethod,
                                                            stateObj, IntPtr.Zero, ref exc);
                if (exc != IntPtr.Zero || boxedStateId == IntPtr.Zero
                    || !this.TryUnboxMonoInt32(boxedStateId, out int stateId)
                    || stateId != ForagingAnimSeaPlayerStateId)
                {
                    status = "fsm has no SeaClean state";
                    return false;
                }

                byte phaseArg = phase;
                IntPtr* phaseArgs = stackalloc IntPtr[1];
                phaseArgs[0] = (IntPtr)(&phaseArg);
                auraMonoRuntimeInvoke(this.foragingAnimSeaSetAnimPhaseMethod, stateObj,
                                      (IntPtr)phaseArgs, ref exc);
                if (exc != IntPtr.Zero)
                {
                    status = "SetAnimPhase failed";
                    return false;
                }

                // The other half of what a watcher gets: with a type set, their copy of the clip
                // plays that pollutant's debris VFX and sound; left at 0, PlayAdditionalToolVfx
                // returns before it starts anything. Cleared with the phase, so nothing keeps
                // claiming a target after we stop.
                if (this.foragingAnimSeaSetTargetTypeMethod != IntPtr.Zero)
                {
                    int typeArg = (phase != ForagingAnimSeaPhaseNone
                                   && Time.unscaledTime - this.foragingAnimSeaTargetSeenAt
                                      <= ForagingAnimSeaTargetFreshSeconds)
                        ? this.foragingAnimSeaTargetTypeId
                        : 0;
                    IntPtr* typeArgs = stackalloc IntPtr[1];
                    typeArgs[0] = (IntPtr)(&typeArg);
                    exc = IntPtr.Zero;
                    auraMonoRuntimeInvoke(this.foragingAnimSeaSetTargetTypeMethod, stateObj,
                                          (IntPtr)typeArgs, ref exc);
                }

                if (motion == ForagingAnimSeaMotion.RestoreIfLost
                    && this.IsForagingAnimSeaMotionOurs(player))
                {
                    motion = ForagingAnimSeaMotion.None;
                }

                if (motion == ForagingAnimSeaMotion.Set
                    || motion == ForagingAnimSeaMotion.RestoreIfLost)
                {
                    if (!this.TrySetForagingAnimSeaMotionFrom(player, stateObj, pins, out status))
                    {
                        return false;
                    }
                }
                else if (motion == ForagingAnimSeaMotion.Restore)
                {
                    // Back to whatever state the FSM is really in. This is also what makes
                    // PlayerSeaCleanMotion.OnMotionFinish fire StopClean and drop the tool VFX.
                    IntPtr current = auraMonoRuntimeInvoke(this.foragingAnimSeaGetCurrentStateMethod,
                                                            player, IntPtr.Zero, ref exc);
                    if (exc != IntPtr.Zero || current == IntPtr.Zero)
                    {
                        // The phase is already down, so the character stops scrubbing either way;
                        // it just keeps the cleaner pose until the FSM changes state on its own.
                        ModLogger.Msg("[ForagingAnim] sea clean motion not restored: no current state");
                    }
                    else
                    {
                        HoldForagingAnimSeaPin(current, pins);
                        if (!this.TrySetForagingAnimSeaMotionFrom(player, current, pins,
                                out string restoreStatus))
                        {
                            ModLogger.Msg("[ForagingAnim] sea clean motion not restored: " + restoreStatus);
                        }
                    }
                }

                if (this.foragingAnimSeaForceSyncMethod != IntPtr.Zero)
                {
                    exc = IntPtr.Zero;
                    auraMonoRuntimeInvoke(this.foragingAnimSeaForceSyncMethod, player,
                                          IntPtr.Zero, ref exc);
                }

                status = "ok";
                return true;
            }
            finally
            {
                FreeAuraMonoPins(pins);
            }
        }

        // get_motionContext is declared on PlayerStateBase, so the handle resolved on
        // PlayerStateSeaClean drives any state object.
        private unsafe bool TrySetForagingAnimSeaMotionFrom(IntPtr player, IntPtr stateObj,
                                                            List<uint> pins, out string status)
        {
            IntPtr exc = IntPtr.Zero;
            IntPtr ctx = auraMonoRuntimeInvoke(this.foragingAnimSeaGetMotionCtxMethod, stateObj,
                                               IntPtr.Zero, ref exc);
            if (exc != IntPtr.Zero || ctx == IntPtr.Zero)
            {
                status = "motionContext null";
                return false;
            }

            HoldForagingAnimSeaPin(ctx, pins);

            int part = ForagingAnimSeaCharacterPartBody;
            IntPtr* args = stackalloc IntPtr[2];
            args[0] = (IntPtr)(&part);
            args[1] = ctx;
            auraMonoRuntimeInvoke(this.foragingAnimSeaSetMotionMethod, player, (IntPtr)args, ref exc);
            if (exc != IntPtr.Zero)
            {
                status = "SetMotion failed";
                return false;
            }

            status = "ok";
            return true;
        }

        // Is the body still playing OUR motion? Compared by the context's CLASS rather than its
        // pointer: the pointer is a moving-GC address that means nothing between calls, while the
        // class handle lives as long as the image.
        private bool IsForagingAnimSeaMotionOurs(IntPtr player)
        {
            if (this.foragingAnimSeaGetActionGraphMethod == IntPtr.Zero
                || this.foragingAnimSeaMotionArgClass == IntPtr.Zero
                || auraMonoObjectGetClass == null)
            {
                return false; // cannot tell — re-assert rather than leave a dropped motion
            }

            IntPtr exc = IntPtr.Zero;
            IntPtr graph = auraMonoRuntimeInvoke(this.foragingAnimSeaGetActionGraphMethod, player,
                                                 IntPtr.Zero, ref exc);
            if (exc != IntPtr.Zero || graph == IntPtr.Zero)
            {
                return false;
            }

            IntPtr graphClass = auraMonoObjectGetClass(graph);
            IntPtr getMotionCtx = graphClass == IntPtr.Zero
                ? IntPtr.Zero
                : this.FindAuraMonoMethodOnHierarchy(graphClass, "get_motionContext", 0);
            if (getMotionCtx == IntPtr.Zero)
            {
                return false;
            }

            IntPtr ctx = auraMonoRuntimeInvoke(getMotionCtx, graph, IntPtr.Zero, ref exc);
            if (exc != IntPtr.Zero || ctx == IntPtr.Zero)
            {
                return false;
            }

            return auraMonoObjectGetClass(ctx) == this.foragingAnimSeaMotionArgClass;
        }

        private static void HoldForagingAnimSeaPin(IntPtr obj, List<uint> pins)
        {
            uint pin = AuraMonoPinNew(obj);
            if (pin != 0u)
            {
                pins.Add(pin);
            }
        }

        // ── resolve ─────────────────────────────────────────────────────────────────────────────

        private bool EnsureForagingAnimSeaResolved(out string detail)
        {
            detail = null;
            if (this.foragingAnimSeaSetMotionMethod != IntPtr.Zero)
            {
                return true;
            }

            float now = Time.unscaledTime;
            if (now < this.foragingAnimSeaResolveRetryAt)
            {
                detail = "types not resolved yet";
                return false;
            }

            this.foragingAnimSeaResolveRetryAt = now + ForagingAnimSeaResolveRetry;

            if (!this.EnsureAuraMonoApiReady() || !this.AttachAuraMonoThread()
                || auraMonoRuntimeInvoke == null || auraMonoObjectGetClass == null)
            {
                detail = "AuraMono unavailable";
                return false;
            }

            IntPtr playerClass = this.FindAuraMonoClassInAllLoadedImages(
                "LocalPlayerComponent", "XDTLevelAndEntity.Gameplay.Component.Player");
            IntPtr fsmClass = this.FindAuraMonoClassInAllLoadedImages(
                "PlayerFsMachine", "XDTLevelAndEntity.Game.GameMode");
            IntPtr stateClass = this.FindAuraMonoClassInAllLoadedImages(
                "PlayerStateSeaClean", "XDTLevelAndEntity.Gameplay.Component.Player");
            IntPtr argClass = this.FindAuraMonoClassInAllLoadedImages(
                "PlayerSeaCleanMotionArg", "XDTLevelAndEntity.Gameplay.Locomotion");
            if (playerClass == IntPtr.Zero || fsmClass == IntPtr.Zero || stateClass == IntPtr.Zero)
            {
                detail = "class unresolved (player=" + (playerClass != IntPtr.Zero)
                         + " fsm=" + (fsmClass != IntPtr.Zero)
                         + " state=" + (stateClass != IntPtr.Zero) + ")";
                return false;
            }

            IntPtr getState = this.FindAuraMonoMethodOnHierarchy(fsmClass, "GetState", 1);
            IntPtr getStateId = this.FindAuraMonoMethodOnHierarchy(stateClass, "get_State", 0);
            IntPtr getMotionCtx = this.FindAuraMonoMethodOnHierarchy(stateClass, "get_motionContext", 0);
            IntPtr setAnimPhase = this.FindAuraMonoMethodOnHierarchy(stateClass, "SetAnimPhase", 1);
            IntPtr setMotion = this.FindAuraMonoMethodOnHierarchy(playerClass, "SetMotion", 2);
            if (getState == IntPtr.Zero || getStateId == IntPtr.Zero || getMotionCtx == IntPtr.Zero
                || setAnimPhase == IntPtr.Zero || setMotion == IntPtr.Zero)
            {
                detail = "method unresolved (GetState=" + (getState != IntPtr.Zero)
                         + " get_State=" + (getStateId != IntPtr.Zero)
                         + " get_motionContext=" + (getMotionCtx != IntPtr.Zero)
                         + " SetAnimPhase=" + (setAnimPhase != IntPtr.Zero)
                         + " SetMotion=" + (setMotion != IntPtr.Zero) + ")";
                return false;
            }

            this.foragingAnimSeaPlayerClass = playerClass;
            this.foragingAnimSeaMotionArgClass = argClass;
            this.foragingAnimSeaGetStateMethod = getState;
            this.foragingAnimSeaGetStateIdMethod = getStateId;
            this.foragingAnimSeaGetMotionCtxMethod = getMotionCtx;
            this.foragingAnimSeaSetAnimPhaseMethod = setAnimPhase;

            // Optional: without it the pose and triggers still replicate, only the debris VFX is
            // missing, so a rename must not take the whole animation down with it.
            this.foragingAnimSeaSetTargetTypeMethod =
                this.FindAuraMonoMethodOnHierarchy(stateClass, "SetTargetTypeId", 1);

            // Optional: without these the animation still plays, it just cannot self-repair
            // (get_actionGraph) or flush the status early (ForceSyncStatus).
            this.foragingAnimSeaGetCurrentStateMethod =
                this.FindAuraMonoMethodOnHierarchy(playerClass, "GetCurrentState", 0);
            this.foragingAnimSeaGetActionGraphMethod =
                this.FindAuraMonoMethodOnHierarchy(playerClass, "get_actionGraph", 0);
            this.foragingAnimSeaForceSyncMethod =
                this.FindAuraMonoMethodOnHierarchy(playerClass, "ForceSyncStatus", 0);

            // Set LAST: it is the flag the fast path checks, so a partial resolve never looks done.
            this.foragingAnimSeaSetMotionMethod = setMotion;
            return true;
        }
    }
}
