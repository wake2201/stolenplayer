using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using ThiefSim2Multiplayer.Core;
using ThiefSim2Multiplayer.Core.Networking;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed class SceneContinuityController : IDisposable
    {
        private static SceneContinuityController current;
        private readonly NetworkManager network;
        private readonly Action<string> log;
        private readonly Harmony harmony = new Harmony(Plugin.Id + ".scene-continuity");
        private readonly Type gmType = Type.GetType("GameManager, Assembly-CSharp", true);
        private static readonly MethodInfo prefsGet = AccessTools.Method(Type.GetType("SPrefs, Assembly-CSharp", true), "GetInt", new[] { typeof(string) });
        private AsyncOperation loading;
        private bool boundaryPending;
        private Component manager;
        private readonly PlayerLocator locator=new PlayerLocator();
        private readonly CooperativeTimeController time;
        private string readyCandidate="";
        private Component candidatePlayer, candidateManager;
        private GameObject candidateCar;
        private string waitingReason="";
        private bool clientNeedsSpawn;
        private static string notice = "";
        internal SceneContinuityController(NetworkManager n, Action<string> logger)
        {
            network = n; log = logger; current = this; time=new CooperativeTimeController(n,logger);
            network.HostSceneRequested += Follow;
            harmony.Patch(AccessTools.Method(gmType, "Start"), transpiler: new HarmonyMethod(typeof(SceneContinuityController), nameof(InitializationReads)));
            harmony.Patch(AccessTools.Method(Type.GetType("VehiclesManager, Assembly-CSharp",true),"InitiateMainCar"),transpiler:new HarmonyMethod(typeof(SceneContinuityController),nameof(InitializationReads)));
            harmony.Patch(AccessTools.Method(Type.GetType("SaveGame, Assembly-CSharp",true),"LoadGame"),transpiler:new HarmonyMethod(typeof(SceneContinuityController),nameof(InitializationReads)));
            harmony.Patch(AccessTools.Method(Type.GetType("CarController, Assembly-CSharp",true),"IsStartingCar"),prefix:new HarmonyMethod(typeof(SceneContinuityController),nameof(ClientInitialSeat)));
            foreach (string method in new[] { "LoadWait", "LoadMain" }) harmony.Patch(AccessTools.Method(gmType, method), prefix: new HarmonyMethod(typeof(SceneContinuityController), nameof(BeforeReload)));
            // Intercept selection before its save writes. Client cannot select a different global world.
            foreach (var method in gmType.GetMethods(BindingFlags.Instance | BindingFlags.Public))
                if (method.Name.StartsWith("Select", StringComparison.Ordinal) && method.GetParameters().Length == 0 && method.ReturnType == typeof(void))
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(SceneContinuityController), nameof(ClientTravelIntent)));
            foreach (string method in new[] { "Begin", "PassStart" }) harmony.Patch(AccessTools.Method(Type.GetType("PassTime, Assembly-CSharp", true), method), prefix: new HarmonyMethod(typeof(SceneContinuityController), nameof(SleepIntent)));
            foreach (string method in new[] { "Sleep", "SleepB", "LayDown" }) harmony.Patch(AccessTools.Method(Type.GetType("Bed, Assembly-CSharp", true), method), prefix: new HarmonyMethod(typeof(SceneContinuityController), nameof(SleepIntent)));
        }
        private static bool Mission => current != null && current.network.Scenes.Target != null && current.network.Scenes.Target.Map != "PlayerHideout";
        // Native Host mission completion remains authoritative and follows the
        // original destination flow. Clients cannot choose a divergent destination.
        private static bool ClientTravelIntent(MethodBase __originalMethod) => current == null || current.network.Session.State == SessionState.Offline || current.network.Session.IsHost;
        private static bool SleepIntent(Component __instance,MethodBase __originalMethod)
        {
            return current==null || current.time.Intent(__instance,__originalMethod,Mission);
        }
        internal void DrawNotice() { if (network.Session.State!=SessionState.Offline && notice.Length > 0) GUI.Label(new Rect(20, Screen.height-80, 700, 28), notice); }
        private static bool BeforeReload()
        {
            if (current == null || current.network.Session.State == SessionState.Offline) return true;
            if (!current.network.Session.IsHost) return false;
            var target = NetworkSceneDescriptor.FromLocation((int)prefsGet.Invoke(null, new object[] { "CurrentLocation" }));
            if (target == null) { current.network.FailSceneTransition("Unsupported CurrentLocation at vanilla reload."); return false; }
            target=current.HostDescriptor(target.Map);
            if (!current.network.Scenes.Active || current.network.Scenes.Target.Map != target.Map || current.network.Scenes.LocalLoaded || current.network.Scenes.State == SceneTransitionState.Failed) {
                current.network.BeginSceneTransition(target);
            }
            return true;
        }
        private static IEnumerable<CodeInstruction> InitializationReads(IEnumerable<CodeInstruction> code)
        {
            var replacement = AccessTools.Method(typeof(SceneContinuityController), nameof(InitializationInt));
            foreach (var instruction in code) { if (instruction.Calls(prefsGet)) instruction.operand = replacement; yield return instruction; }
        }
        private static int InitializationInt(string key)
        {
            var c = current;
            if (c != null && !c.network.Session.IsHost && c.network.Session.State == SessionState.Connected && c.network.Scenes.Active)
            {
                if (key == "CurrentLocation") return c.network.Scenes.Target.Location;
                if(key=="CurrentCar") return c.network.Scenes.Target.VehicleVariant;
                if(key=="VanBought") return c.network.Scenes.Target.SecondVehicleAvailable ? 1 : 0;
                if (key == "Saved_Pos") return 0; // original map branch chooses its own verified spawn, never the old map's saved position.
            }
            return (int)prefsGet.Invoke(null, new object[] { key });
        }
        private static bool ClientInitialSeat(Component __instance)
        {
            if(SharedVehicleController.Current?.RestoringDestinationDriver(__instance)==true) return true;
            var c=current;
            if(c != null && c.network.PeerConnected && c.network.Scenes.Active && !c.network.Scenes.LocalLoaded) { c.clientNeedsSpawn=!c.network.Session.IsHost; return false; }
            if(c==null || c.network.Session.IsHost || c.network.Session.State!=SessionState.Connected || !c.network.Scenes.Active) return true;
            c.clientNeedsSpawn=true; return false;
        }
        private void Follow(NetworkSceneDescriptor target)
        {
            using var phase=network.FrameProfile.Measure(Core.Diagnostics.RuntimePhase.NativeSceneInitialization);
            try
            {
                string key = LoadedKey();
                if (key == target.SceneKey && manager && GameAccess.Get(GameAccess.Get(manager,"VM"),"currentCarObj")==GameAccess.Get(manager,"Car")) {
                    // Reinitialize even an identical map: its profile-selected car and manager state may differ.
                    log("[TS2MP] SCENE joining existing map through fresh native initialization");
                }
                if (loading != null && !loading.isDone) { network.FailSceneTransition("A vanilla scene operation was already active."); return; }
                var main = SceneManager.GetSceneByName("MainScene"); var menu = SceneManager.GetSceneByName("MainMenuNew");
                if (!main.isLoaded && !menu.isLoaded) { network.FailSceneTransition("Join requires initialized MainMenuNew profile or stable gameplay."); return; }
                if (menu.isLoaded)
                {
                    var menuType = Type.GetType("MainMenu, Assembly-CSharp", true);
                    var menuComponent = UnityEngine.Object.FindObjectOfType(menuType) as Component;
                    var button = menuComponent ? GameAccess.Get(menuComponent, "ContinueButton") : null;
                    if (button == null || !(bool)button.GetType().GetProperty("interactable").GetValue(button, null))
                    { network.FailSceneTransition("MainMenuNew has no initialized playable profile (ContinueButton disabled); initialize a local profile once before joining."); return; }
                }
                // Initial car seating is story-gated in vanilla. Joining must also choose an
                // explicit outside-car spawn for a profile that has not reached that story gate.
                clientNeedsSpawn=target.Map!="PlayerHideout";
                loading = SceneManager.LoadSceneAsync("MainScene", LoadSceneMode.Single);
                log("[TS2MP] SCENE automatic follow target=" + target.SceneKey + "; vanilla GameManager.Start map/spawn initialization; preferences not written");
            }
            catch (Exception e) { network.FailSceneTransition("Native load failed: " + e.GetType().Name); }
        }
        internal void Boundary()
        {
            manager = null;
            time.Release(); ClearCandidate();
            if(SceneManager.GetSceneByName("MainMenuNew").isLoaded && (loading==null || loading.isDone) && network.Session.State!=SessionState.Offline) { network.Disconnect("Returned to native menu."); network.Scenes.Clear(); return; }
            boundaryPending = true;
            // During an announced transition callbacks invalidate presentation only. The network epoch changes once.
            if (network.Session.IsHost && network.Scenes.Active && network.Scenes.LocalLoaded)
            { network.FailSceneTransition("Unannounced scene boundary; awaiting supported stable world."); }
        }
        internal bool Tick(bool actorAvailable, string key)
        {
            using var phase=network.FrameProfile.Measure(Core.Diagnostics.RuntimePhase.WorldDescriptorResolution);
            if (network.Session.State == SessionState.Offline) { notice=""; return actorAvailable; }
            // A failed load stays visibly closed until a new transition/session. Never
            // attach occupants or repeatedly initialize entities for its old descriptor.
            if (network.Scenes.State == SceneTransitionState.Failed) return Waiting("transition failed");
            if (!actorAvailable || (loading != null && !loading.isDone)) return Waiting("native actor/load unavailable");
            if (network.Scenes.Active && key != network.Scenes.Target.SceneKey) return Waiting("scene set differs from target");
            if (!boundaryPending && network.Scenes.LocalLoaded) { time.Tick(); return network.Scenes.State != SceneTransitionState.Failed && key == network.Scenes.Target.SceneKey; }
            if (!manager) manager = UnityEngine.Object.FindObjectOfType(gmType) as Component;
            var gm = manager;
            var player=locator.Find();
            if (!gm || !player || !GameAccess.Bool(gm, "loadedMap") || GameAccess.Get(gm,"PS") as Component!=player || Time.timeScale<=0 ||
                !GameAccess.Bool(GameAccess.Get(player,"saveGameScript"),"loadedGame") || GameAccess.Get(player,"rewiredPlayer")==null ||
                !(GameAccess.Get(player,"MainCam") as Camera) || !(GameAccess.Get(player,"PMB") as Behaviour) || !(GameAccess.Get(player,"PCB") as Behaviour) ||
                GameAccess.Get(player,"invScript")==null || !(GameAccess.Get(gm,"Car") as GameObject) || !(GameAccess.Get(GameAccess.Get(gm,"VM"),"currentCarObj") as GameObject) ||
                !(GameAccess.Get(player,"item_IDs2") is Array items) || items.Length==0) return Waiting("native manager/player initialization incomplete");
            var carObject=GameAccess.Get(gm,"Car") as GameObject;
            var vm=GameAccess.Get(gm,"VM");
            var car=carObject.GetComponent(Type.GetType("CarController, Assembly-CSharp",true));
            if ((network.Scenes.Target?.HasTravelOccupants==true && !carObject.activeInHierarchy) || !car || GameAccess.Get(vm,"currentCarObj") as GameObject!=carObject ||
                (carObject.activeInHierarchy && GameAccess.Get(car,"PS") as Component!=player) || !player.GetComponent<CharacterController>()) return Waiting("native canonical vehicle/actor references incomplete");
            // Observe the same fully initialized native objects on consecutive frames
            // BEFORE applying driver entry or passenger suspension.
            if(readyCandidate!=key || candidatePlayer!=player || candidateManager!=gm || candidateCar!=carObject) {
                readyCandidate=key; candidatePlayer=player; candidateManager=gm; candidateCar=carObject; return false;
            }
            if(!SharedVehicleController.Current.RestoreDestination(player)) return false;
            if(clientNeedsSpawn && !GameAccess.Bool(player,"is_in_vehicle")) {
                var exit=GameAccess.Get(car,"leave_car_transform") as Transform; RaycastHit ground;
                if(!exit || !Physics.Raycast(exit.position+Vector3.up*2,Vector3.down,out ground,5,~0,QueryTriggerInteraction.Ignore)) return false;
                var cc=player.GetComponent<CharacterController>();
                player.transform.position=ground.point+Vector3.up*(cc.height*.5f-cc.center.y+.1f);
                player.transform.rotation=exit.rotation; GameAccess.Call(GameAccess.Get(player,"PCB"),"ResetCamRot");
                clientNeedsSpawn=false; log("[TS2MP] SCENE Client native spawn outside vehicle; no implicit seat assignment");
            }
            clientNeedsSpawn=false;
            time.Bind(gm); time.Tick();
            // Check once per boundary/acquisition; normal telemetry does not scan scenes or managers.
            if (network.Session.IsHost && !network.Scenes.Active)
            {
                NetworkSceneDescriptor descriptor;
                if (!NetworkSceneDescriptor.TryFromKey(key, out descriptor)) { network.FailSceneTransition("Loaded scene set unsupported: " + key); boundaryPending = false; return false; }
                descriptor=HostDescriptor(descriptor.Map);
                network.PublishStableScene(descriptor);
            }
            boundaryPending = false;
            waitingReason="";
            if (network.Scenes.Active) return network.MarkSceneLoaded(key);
            return false;
        }
        private void ClearCandidate() { readyCandidate=""; candidatePlayer=candidateManager=null; candidateCar=null; }
        private bool Waiting(string reason) {
            ClearCandidate();
            if(waitingReason!=reason) { waitingReason=reason; log("[TS2MP] SCENE waiting: "+reason); }
            return false;
        }
        internal void Audit()
        {
            var actor=locator.Find(); var gm=actor ? GameAccess.Get(actor,"GM") as Component : null;
            var vm=gm ? GameAccess.Get(gm,"VM") : null; var obj=gm ? GameAccess.Get(gm,"Car") as GameObject : null;
            var car=obj ? obj.GetComponent(Type.GetType("CarController, Assembly-CSharp",true)) : null;
            log("[TS2MP] SCENE readiness audit: wait="+waitingReason+" key="+LoadedKey()+" target="+network.Scenes.Target?.SceneKey+
                " actor="+(actor ? actor.GetInstanceID() : 0)+" manager="+(gm ? gm.GetInstanceID() : 0)+" selectedManager="+(manager ? manager.GetInstanceID() : 0)+
                " car="+(obj ? obj.GetInstanceID() : 0)+" carActive="+(obj && obj.activeInHierarchy)+" carActor="+(car ? GameAccess.Get(car,"PS") : null)+
                " carBinding="+car+" currentCar="+(vm!=null ? GameAccess.Get(vm,"currentCarObj") : null));
        }
        private NetworkSceneDescriptor HostDescriptor(string map) => new NetworkSceneDescriptor(map,(byte)(int)prefsGet.Invoke(null,new object[] {"CurrentCar"}),(int)prefsGet.Invoke(null,new object[] {"VanBought"})==1);
        internal void OpenPassengerSleep(Component actor) => time.OpenPassengerSleep(actor);
        private static string LoadedKey()
        {
            var names = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++) { var s = SceneManager.GetSceneAt(i); if (s.isLoaded && !string.IsNullOrEmpty(s.path)) names.Add(s.name); }
            names.Sort(StringComparer.Ordinal); return string.Join("|", names.ToArray());
        }
        public void Dispose() { ClearCandidate(); manager=null; time.Dispose(); network.HostSceneRequested -= Follow; harmony.UnpatchSelf(); if (current == this) current = null; }
    }
}
