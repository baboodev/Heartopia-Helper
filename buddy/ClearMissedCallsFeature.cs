using System;
using System.Collections.Generic;

namespace HeartopiaMod
{
    // Clear Missed Calls — one button that empties the missed-call list on the player's watch.
    // Full research record: docs/plans/2026-09-07-clear-missed-calls.md.
    //
    // WHAT THE LIST IS. The watch (XDTGame.UI.Panel.WatchPanel) carries a phone app —
    // PhoneWatchAppWidget over UnAnswerPhoneCellWidget rows — and every row comes out of
    // DataModule<PhoneSystem> (XDTLevelAndEntity.Game.Module.Phone). Two lists feed it:
    //
    //   _recallData    missed INVITES — EventCallData (activity events), PartyCallData,
    //                  SelfRoomInviteCallData, MultiBuildCallData. Appended by AddRecallData from
    //                  PhoneModule.DropCall when a call is dropped or rings out. Pure client state.
    //   _taskCallData  quest calls. NOT clearable in any lasting sense: InitUnAnswerCall() rebuilds
    //                  it from TaskSystem on every TaskUpdated, on PhoneModule.OnInitialize, and
    //                  whenever the phone app is opened. Clearing it would also be wrong — those
    //                  rows are live quest hints.
    //
    // So the button REMOVES the invites and MUTES the quest calls (the red point, not the row) —
    // which is exactly the split the game itself makes.
    //
    // LIFETIME. PhoneSystem is [ModuleScope(typeof(GameLevel_Login))] and GameLevel_Login is the
    // ROOT of the level tree (LevelDefine: Login -> Main -> {MicroHome -> Craft, Craft}), so the
    // module is built once per login and torn down only on the way back to it. The list survives
    // Town <-> MicroHome <-> Craft, is never persisted and is never sent to the server: a clear
    // holds until logout, which is all the lifetime the list ever had.
    //
    // LEVER: PhoneSystem.RemoveInviteCall(PhoneCallData) — the call the GAME makes from
    // PhoneEndAction() on PartyCallData / SelfRoomInviteCallData / MultiBuildCallData. It matches by
    // the entry's own IsSameCall (so it is type-agnostic and takes EventCallData too), removes it,
    // and routes through RefreshReCallDataRedPoint, which deactivates the entry's red point. Red
    // points are a client-side node tree (RedPointManager.UpdateRedPointData -> SetSelfActive):
    // NOTHING leaves the client, this feature sends zero commands.
    //
    // THREE TRAPS, all avoided here:
    //  1. NEVER call PhoneCallData.PhoneEndAction(). Despite the name it is the ACCEPT path:
    //     SelfRoomInviteCallData -> LoginSystem.JoinToFriendRoom; MultiBuildCallData ->
    //     JoinMultiBuild / MoveToAnotherTownAndJoin; PartyCallData -> opens the party panels;
    //     TaskCallData -> ClientAcceptTask / ClientSubmitTask. A "clear" loop built on it would
    //     join rooms and submit quests.
    //  2. NEVER invoke the abstract members of PhoneCallData (GetCallerName, GetDescription,
    //     IsSameCall, PhoneEndAction). Measured on the running build: mono_runtime_invoke on the
    //     abstract declaration raises System.BadImageFormatException — it needs the concrete method
    //     (mono_object_get_virtual_method). Not needed: IsSameCall is called by the game from
    //     inside RemoveInviteCall.
    //  3. NEVER List<T>.Clear() the field. That means inflating a BCL generic (the
    //     auramono-bcl-generic-typegettype-crash trap) AND it would strand every registered red
    //     point active for the rest of the session, because RefreshReCallDataRedPoint's
    //     deactivation loop walks the list — which would by then be empty.
    //
    // WATCH-OPEN GUARD. PhoneWatchAppWidget.ShowData() takes its OWN List<PhoneCallData> copy, so
    // clearing behind an open panel leaves rows whose "call back" button would run
    // AnswerCallAction(isCallback: true) on a dead invite. The button refuses while WatchPanel is
    // open (IUIManager.GetView(Type) != null, through the resolver PersistentHudFeature already
    // ships). It FAILS OPEN when the UI manager cannot be resolved: the hazard is one stale row,
    // and a guard that cannot answer must not make the button dead.
    //
    // Cost: no detour, no .text patch, no event subscription, no per-frame work, no persisted
    // config. Three public methods of a client-side DataModule, invoked on a click.
    public partial class HeartopiaComplete
    {
        private const string ClearMissedCallsTag = "ClearMissedCalls";

        private const string ClearMissedCallsPhoneSystemTypeName = "XDTLevelAndEntity.Game.Module.Phone.PhoneSystem";
        private const string ClearMissedCallsWatchPanelTypeName = "XDTGame.UI.Panel.WatchPanel";

        // Class/method pointers only (CI lint E3) — image lifetime, so they survive a world change.
        // The DataModule instance is level-scoped and is re-resolved on every click, never cached.
        private IntPtr clearMissedCallsPhoneSystemClass = IntPtr.Zero;
        private IntPtr clearMissedCallsGetRecallCountMethod = IntPtr.Zero;
        private IntPtr clearMissedCallsRemoveInviteMethod = IntPtr.Zero;
        private IntPtr clearMissedCallsReadUnAnswerMethod = IntPtr.Zero;
        private IntPtr clearMissedCallsRefreshRedPointMethod = IntPtr.Zero;

        // Last run's outcome. Kept as numbers + an untranslated technical reason, and composed into
        // the status line at READ time, so a language switch retranslates the frame around it.
        private bool clearMissedCallsRan;
        private string clearMissedCallsFailure;
        private int clearMissedCallsRemoved;
        private int clearMissedCallsMuted;
        private int clearMissedCallsBefore;
        private int clearMissedCallsAfter;

        // The bare sentence — the UI wraps it in its own "Status: {0}" frame and reuses it verbatim
        // for the toast. Composed at READ time so a language switch retranslates it; the technical
        // failure reason inside stays English, like every other status in the mod.
        internal string GetClearMissedCallsStatus()
        {
            if (this.clearMissedCallsFailure != null)
            {
                return this.LF("failed — {0}", this.clearMissedCallsFailure);
            }

            if (!this.clearMissedCallsRan)
            {
                return this.L("idle — nothing cleared yet.");
            }

            return this.LF("{0} invite(s) cleared, {1} quest call(s) muted — {2} → {3} left.",
                this.clearMissedCallsRemoved, this.clearMissedCallsMuted,
                this.clearMissedCallsBefore, this.clearMissedCallsAfter);
        }

        // The button. Returns true when the pass COMPLETED — an empty list is a success, not a
        // failure, so the toast stays green when there was simply nothing to clear. How much was
        // actually touched is in the status line and in the log.
        internal bool ClearMissedCalls()
        {
            string failure = null;
            int removed = 0;
            int muted = 0;
            int before = 0;
            int after = 0;

            try
            {
                if (!this.IsWorldReady)
                {
                    failure = "world not ready";
                }
                else if (!this.EnsureAuraMonoApiReady() || !this.AttachAuraMonoThread()
                    || auraMonoRuntimeInvoke == null || auraMonoObjectGetClass == null)
                {
                    failure = "Mono API not ready";
                }
                else if (!AuraMonoGameDataLive)
                {
                    failure = "game Mono side not live yet";
                }
                else if (!AuraMonoPinningAvailable)
                {
                    // Every step below hands out MonoObject* across an invoke that can allocate.
                    // Without gchandles they would race relocation — fail closed, loudly.
                    failure = "AuraMono pinning unavailable";
                }
                else if (this.IsClearMissedCallsWatchOpen())
                {
                    failure = "the watch is open — close it first";
                }
                else if (this.TryResolveClearMissedCallsMethods(out failure))
                {
                    failure = this.RunClearMissedCalls(out removed, out muted, out before, out after);
                }
            }
            catch (Exception ex)
            {
                failure = "exception: " + ex.Message;
            }

            this.clearMissedCallsRan = true;
            this.clearMissedCallsFailure = failure;
            this.clearMissedCallsRemoved = removed;
            this.clearMissedCallsMuted = muted;
            this.clearMissedCallsBefore = before;
            this.clearMissedCallsAfter = after;

            if (failure != null)
            {
                // Tier 1: failure text is always logged, never gated, never toast-only.
                FeatureLog.Fail(ClearMissedCallsTag, "clear refused: " + failure);
                return false;
            }

            FeatureLog.Life(ClearMissedCallsTag,
                "Cleared " + removed + " invite(s), muted " + muted + " quest call(s) — "
                + before + " -> " + after + " on the watch.");
            return true;
        }

        // The three passes, with the instance pinned across all of them. Returns null on success or
        // the technical reason it stopped.
        private string RunClearMissedCalls(out int removed, out int muted, out int before, out int after)
        {
            removed = 0;
            muted = 0;
            before = 0;
            after = 0;

            IntPtr instance = this.TryGetAuraMonoDataModuleInstance(this.clearMissedCallsPhoneSystemClass);
            if (instance == IntPtr.Zero)
            {
                return "DataModule<PhoneSystem>.Instance null";
            }

            uint instancePin = AuraMonoPinNew(instance);
            try
            {
                before = this.GetAuraMonoIntCount(instance, this.clearMissedCallsGetRecallCountMethod);

                // Invites: the real removal.
                string failure = this.TryApplyClearMissedCalls(instance, "_recallData",
                    this.clearMissedCallsRemoveInviteMethod, "RemoveInviteCall", out removed);
                if (failure != null)
                {
                    return failure;
                }

                // Quest calls: mark read, exactly what PhoneWatchAppWidget.RefreshRedPoint does when
                // the phone app closes. The rows survive (they are live quest hints and would come
                // back on the next InitUnAnswerCall anyway); only the red point goes.
                failure = this.TryApplyClearMissedCalls(instance, "_taskCallData",
                    this.clearMissedCallsReadUnAnswerMethod, "ReadUnAnswerCall", out muted);
                if (failure != null)
                {
                    return failure;
                }

                // One sweep to settle the red points, including the ones the passes above marked.
                failure = this.TryRefreshClearMissedCallsRedPoints(instance);
                if (failure != null)
                {
                    return failure;
                }

                after = this.GetAuraMonoIntCount(instance, this.clearMissedCallsGetRecallCountMethod);
                return null;
            }
            finally
            {
                AuraMonoPinFree(instancePin);
            }
        }

        // One pass: read a List<PhoneCallData> field off PhoneSystem, snapshot it PINNED, and invoke
        // a 1-argument method with each entry.
        //
        // Snapshot-then-invoke, not iterate-and-remove: the enumeration pins the collection and every
        // item, so the entries stay put while RemoveInviteCall mutates the list underneath them.
        // Calling it for an entry a previous call already took out as a duplicate (IsSameCall
        // matches more than one row) is a no-op, so the pass is idempotent. Nothing yields here, so
        // the raw-pointers-across-yields rule does not apply.
        private unsafe string TryApplyClearMissedCalls(IntPtr instance, string listFieldName,
                                                       IntPtr method, string methodLabel, out int touched)
        {
            touched = 0;

            if (!this.TryGetMonoObjectMember(instance, listFieldName, out IntPtr listObj) || listObj == IntPtr.Zero)
            {
                // Both lists are newed up in PhoneSystem's field initialisers, so a null here is a
                // shape change in the game build, not an empty list.
                return "PhoneSystem." + listFieldName + " unreadable";
            }

            uint listPin = AuraMonoPinNew(listObj);
            try
            {
                IntPtr listClass = auraMonoObjectGetClass(listObj);
                IntPtr getCount = listClass != IntPtr.Zero
                    ? this.FindAuraMonoMethodOnHierarchy(listClass, "get_Count", 0)
                    : IntPtr.Zero;
                if (getCount == IntPtr.Zero)
                {
                    return listFieldName + ".get_Count not found";
                }

                // TryEnumerateAuraMonoCollectionItems returns false for an EMPTY list too, so the
                // count decides which one this is (auramono-enumerate-empty-vs-failed).
                int count = this.GetAuraMonoIntCount(listObj, getCount);
                if (count <= 0)
                {
                    return null;
                }

                List<IntPtr> entries = new List<IntPtr>();
                List<uint> pins = new List<uint>();
                bool enumerated = this.TryEnumerateAuraMonoCollectionItems(listObj, entries, pins);
                try
                {
                    if (!enumerated)
                    {
                        return listFieldName + " enumeration failed (get_Count=" + count + ")";
                    }

                    // Hoisted: a stackalloc inside the loop would grow the frame per entry.
                    IntPtr* args = stackalloc IntPtr[1];
                    for (int i = 0; i < entries.Count; i++)
                    {
                        IntPtr entry = entries[i];
                        if (entry == IntPtr.Zero)
                        {
                            continue;
                        }

                        // Reference-type parameter: mono_runtime_invoke takes the object pointer
                        // itself, not its address (auramono-invoke-out-params).
                        args[0] = entry;
                        IntPtr exc = IntPtr.Zero;
                        auraMonoRuntimeInvoke(method, instance, (IntPtr)args, ref exc);
                        if (exc != IntPtr.Zero)
                        {
                            return methodLabel + " threw on entry " + i + " of " + entries.Count;
                        }

                        touched++;
                    }

                    return null;
                }
                finally
                {
                    FreeAuraMonoPins(pins);
                }
            }
            finally
            {
                AuraMonoPinFree(listPin);
            }
        }

        // RefreshReCallDataRedPoint(PhoneCallData phoneCallData = null) — one parameter with a
        // default, so the null has to be passed explicitly.
        private unsafe string TryRefreshClearMissedCallsRedPoints(IntPtr instance)
        {
            IntPtr* args = stackalloc IntPtr[1];
            args[0] = IntPtr.Zero;
            IntPtr exc = IntPtr.Zero;
            auraMonoRuntimeInvoke(this.clearMissedCallsRefreshRedPointMethod, instance, (IntPtr)args, ref exc);
            return exc != IntPtr.Zero ? "RefreshReCallDataRedPoint threw" : null;
        }

        // GetView(Type) != null <=> the panel is open and not Closing/Closed. Fails OPEN (see the
        // file header): an unresolvable UI manager must not make the button dead.
        private bool IsClearMissedCallsWatchOpen()
        {
            try
            {
                if (!this.TryPersistentHudResolveUiManager(out IntPtr uiManagerObj) || uiManagerObj == IntPtr.Zero)
                {
                    FeatureLog.Once(ClearMissedCallsTag, "no-uimanager",
                        "UIManager unavailable — clearing without the watch-open guard.");
                    return false;
                }

                uint pin = AuraMonoPinNew(uiManagerObj);
                try
                {
                    if (!this.TryPersistentHudIsPanelOpen(uiManagerObj, ClearMissedCallsWatchPanelTypeName, out bool isOpen))
                    {
                        FeatureLog.Once(ClearMissedCallsTag, "no-watchpanel",
                            "WatchPanel type unresolved — clearing without the watch-open guard.");
                        return false;
                    }

                    return isOpen;
                }
                finally
                {
                    AuraMonoPinFree(pin);
                }
            }
            catch (Exception ex)
            {
                FeatureLog.Once(ClearMissedCallsTag, "guard-threw",
                    "watch-open guard threw (" + ex.Message + ") — clearing anyway.");
                return false;
            }
        }

        private bool TryResolveClearMissedCallsMethods(out string failure)
        {
            failure = null;

            if (this.clearMissedCallsPhoneSystemClass == IntPtr.Zero)
            {
                this.clearMissedCallsPhoneSystemClass =
                    this.FindAuraMonoClassByFullName(ClearMissedCallsPhoneSystemTypeName);
                if (this.clearMissedCallsPhoneSystemClass == IntPtr.Zero)
                {
                    // The namespace prefix matches the XDTLevelAndEntity image, so the likely-images
                    // probe should hit; sweep every game image before giving up anyway.
                    this.clearMissedCallsPhoneSystemClass =
                        this.FindAuraMonoClassByFullNameExhaustive(ClearMissedCallsPhoneSystemTypeName);
                }
            }

            if (this.clearMissedCallsPhoneSystemClass == IntPtr.Zero)
            {
                failure = "PhoneSystem class not found";
                return false;
            }

            // get_Instance lives on the inflated base DataModule<PhoneSystem>, hence the hierarchy
            // walk inside TryGetAuraMonoDataModuleInstance; the four below are declared on
            // PhoneSystem itself, but the walk costs nothing and survives a base-class move.
            if (this.clearMissedCallsGetRecallCountMethod == IntPtr.Zero)
            {
                this.clearMissedCallsGetRecallCountMethod =
                    this.FindAuraMonoMethodOnHierarchy(this.clearMissedCallsPhoneSystemClass, "GetRecallCount", 0);
            }

            if (this.clearMissedCallsRemoveInviteMethod == IntPtr.Zero)
            {
                this.clearMissedCallsRemoveInviteMethod =
                    this.FindAuraMonoMethodOnHierarchy(this.clearMissedCallsPhoneSystemClass, "RemoveInviteCall", 1);
            }

            if (this.clearMissedCallsReadUnAnswerMethod == IntPtr.Zero)
            {
                this.clearMissedCallsReadUnAnswerMethod =
                    this.FindAuraMonoMethodOnHierarchy(this.clearMissedCallsPhoneSystemClass, "ReadUnAnswerCall", 1);
            }

            if (this.clearMissedCallsRefreshRedPointMethod == IntPtr.Zero)
            {
                this.clearMissedCallsRefreshRedPointMethod =
                    this.FindAuraMonoMethodOnHierarchy(this.clearMissedCallsPhoneSystemClass, "RefreshReCallDataRedPoint", 1);
            }

            if (this.clearMissedCallsGetRecallCountMethod == IntPtr.Zero
                || this.clearMissedCallsRemoveInviteMethod == IntPtr.Zero
                || this.clearMissedCallsReadUnAnswerMethod == IntPtr.Zero
                || this.clearMissedCallsRefreshRedPointMethod == IntPtr.Zero)
            {
                failure = "PhoneSystem methods missing (count="
                    + (this.clearMissedCallsGetRecallCountMethod != IntPtr.Zero)
                    + " remove=" + (this.clearMissedCallsRemoveInviteMethod != IntPtr.Zero)
                    + " read=" + (this.clearMissedCallsReadUnAnswerMethod != IntPtr.Zero)
                    + " refresh=" + (this.clearMissedCallsRefreshRedPointMethod != IntPtr.Zero) + ")";
                return false;
            }

            return true;
        }
    }
}
