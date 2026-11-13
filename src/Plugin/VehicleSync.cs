// M4.1 + M4.2: player-car glue (Unity main thread). Game facts: CODEBASE_MAP §3.7 (VehiclesManager owns the two
// permanent cars, CarController.carNr is their persistent number), §3.9 (travel/arrival), PROJECT_PLAN "Region vehicle rule".
//
// Authority: the host's registry (WorldHost.Fleet / TravelDesk). Each car has one region and at most one holder, the
// instance that has the original car active and simulates it. M5.0: the other instance presents the car on its OWN
// original scene object in "observed" mode (kinematic body moved to the holder's snapshots, EVP driving simulation
// paused, original colliders, doors, enter trigger and seats kept); door state is host-committed ("vdoor").
// Local original car objects are only activated/deactivated (never destroyed or spawned):
//   * travel: GameManager.Select* is decided by the host before the original save/reload runs (client: held until the
//     grant arrives, then resumed exactly once; host: decided in place);
//   * arrival: VehiclesManager.InitiateMainCar activates only the car this instance may simulate, IsStartingCar seats
//     the player only when they brought that car; afterwards the player is placed once at a checked position;
//   * reconcile: whenever the registry changes, activate the held car at its parking pose / deactivate the others;
//     in the hideout, copies of cars that are elsewhere stay as frozen (Rigidbody constraints) travel access points.
// Per-car fuel: the game's single SaveGame.gasTankLeft always belongs to the car this instance drives/holds; values
// are swapped when that car changes and kept per car in the host's parking records (persisted by the host).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    public sealed class VehicleSettings
    {
        public KeyCode TravelMenuKey = KeyCode.F7;
        public string DriverSeatAdjust = "";
        public string PassengerSeatAdjust = "";   // M4.3 local passenger root calibration "carNr:dx,dy,dz;..."
        public bool InteractDebug;              // M4.2.4 bounded E-ray probe (E key press near a proxy, <= 1/s)   // M4.2.3 manual seat calibration "carNr:dx,dy,dz;..."
    }

    internal sealed class VehicleSync : IDisposable, ITravelHandler
    {
        readonly SessionNode _node;
        readonly InstanceLog _log;
        readonly WorldSync _world;
        readonly PlayerSync _players;
        readonly VehicleSettings _cfg;
        readonly string _dataDir;
        readonly bool _host;
        readonly byte _me, _other;
        readonly NetDataWriter _w = new NetDataWriter();
        readonly Stopwatch _clock = Stopwatch.StartNew();

        // ---- compiled accessors ----
        Func<object, UnityEngine.Object> _gmVM, _gmPS, _psVehicle, _gmCar, _gmTownSel, _psTownSel, _psSave, _psWarn, _psCC;
        Func<object, GameObject[]> _gmLeave;
        Func<object, Transform> _carTrunk;
        Func<object, bool> _gmLoaded, _sitting, _engine, _isPlayerCar, _psChase, _psInVehicle;
        Func<object, int> _carNr, _gmDay, _gmWeek;
        Func<object, float> _gas; Action<object, float> _setGas;
        Action<object, GameObject> _setGmCar; Action<object, Transform> _setGmTrunk;
        Action<object> _startVoid, _exitNow, _openTravel;
        Action<object, string> _warn;   // the game's own WarningTextSpawner.SpawnWarning(key) - original texts only
        Func<string, int> _prefInt; Func<string, float> _prefFloat; Action<string, int> _prefSetInt;
        Func<object, string> _vanPref;
        Func<object, bool> _psOpened, _psCaught; Action<object, bool> _setPsOpened; Func<object, float, System.Collections.IEnumerator> _showTown;
        Func<object, UnityEngine.Object> _gmHideoutCar, _vmSecondPos;
        Func<object, UnityEngine.Object> _tppChar, _sitT, _assocT;   // M4.2.2 seat anchors (CarController fields)
        Action<object> _enterVehicle;                               // M4.2.2 resume of the original entry after a handoff
        Func<object, LayerMask> _psMask; Func<object, UnityEngine.Object> _psCam;   // PlayerScript.mask / cam (E raycast), diagnostics only
        // M5.0 original doors (CarController.car_door_L/R, Door fields used by Door.Use / Door.Update) and EVP steering/wheels
        Func<object, UnityEngine.Object> _carDoorL, _carDoorR, _drivetrain, _doorSound, _doorClipOpen, _doorClipClose;
        Func<object, bool> _doorIsOpen, _doorLoadSlow, _doorSlowly;
        Action<object, bool> _doorSetOpen, _doorSetOpening, _doorSetByPlayer, _doorSetCanUse;
        Action<object, float> _doorSetSpeed; Func<object, float> _doorSpeedMul, _doorVolume, _evpSteer;
        FieldInfo _evpWheels, _wCol, _wT, _wSteer;
        Func<object, bool> _loadTrunk; Action<object, bool> _setLoadTrunk;   // CarController.load_trunk_items
        // M4.3 passenger: CarController camera/exit fields, TPP camera, minimap, PlayerScript/motor/camera members used by
        // CarController.EnterVehicle / ExitCar / Set_TPP (IL) - only their non-driver parts are mirrored
        Func<object, UnityEngine.Object> _carMainCam, _carTppCam, _carLeave, _tppCamObj, _gmMinimap, _psPCB, _psHand, _psPMB, _psCamAnim, _psLight;
        Action<object, Transform> _tppSetNewCar; Action<object> _tppDisable, _hideWeapons, _pmbHeight; Action<object, GameObject> _mmSetTarget;
        Action<object, bool> _setCanInteract, _pmbSetCanMove, _pmbSetCrouch, _setTorch; Func<object, bool> _psTorch;
        Action<object, float> _pmbSetVelY;   // M5 hotfix: PlayerMotorBehavior.velocityY reset after a recovery placement
        Action<object, float> _interactDelayed, _pcbEnterRot, _pcbExitRot;
        Func<object, bool> _interact;   // PlayerScript.rewiredPlayer.GetButtonDown("interact"): the original exit action
        // M4.3.1 interaction resolver: the gates of the original E ray branch (PlayerScript.Update IL_46a3..IL_4760), its prompt,
        // the storage call of the CarBox branch (IL_7216..IL_7286) and the Medkit marker component
        Func<object, bool> _psCanInteract, _psCarrying, _psHiding, _psSlowDoor, _psCloseUp, _psHideHands, _pmbRunning, _invShop, _invStorage;
        Func<object, UnityEngine.Object> _psEnterInfo, _psInv, _carBoxHl, _psPrompts; Func<object, int> _carBoxType;
        // M4.3.2b: the original target prompts PlayerScript.Update toggles only inside its ray-hit branch (IL_479b..IL_7f5a);
        // they keep their last state while the ray misses (the prompt canvas just fades out)
        readonly List<Func<object, UnityEngine.Object>> _staleTargetPrompts = new List<Func<object, UnityEngine.Object>>();
        Action<object, int> _psOpenStorage; Action<object, bool> _hlSet; Type _medkitT;
        // M4.3.1 slow car doors (Door.Update / Door.SlowFailed IL)
        Func<object, float> _doorSpeedNow, _doorSlowMul; Func<object, bool> _doorOpening; Func<object, UnityEngine.Object> _doorSqueak; Action<object, float> _doorSetSpeed2;
        bool ResolverApi { get { return _psCanInteract != null && _psCarrying != null && _psHiding != null && _psSlowDoor != null && _psCloseUp != null && _psHideHands != null && _pmbRunning != null
            && _psMask != null && _psCam != null && _psInv != null && _invStorage != null && _invShop != null; } }
        bool SlowDoorApi { get { return _doorSpeedNow != null && _doorSlowMul != null && _doorOpening != null && _doorSetSpeed2 != null; } }
        bool PassengerApi { get { return _carMainCam != null && _carTppCam != null && _carLeave != null && _tppSetNewCar != null && _tppDisable != null && _psPCB != null && _psHand != null && _psPMB != null
            && _setCanInteract != null && _interactDelayed != null && _pmbSetCanMove != null && _pcbEnterRot != null && _pcbExitRot != null && _interact != null && _assocT != null && _sitT != null; } }
        bool DoorApi { get { return _carDoorL != null && _carDoorR != null && _doorIsOpen != null && _doorSetOpen != null && _doorSetOpening != null && _doorSetByPlayer != null && _doorSetCanUse != null && _doorSetSpeed != null && _doorSpeedMul != null && _doorLoadSlow != null && _doorSlowly != null; } }
        readonly List<KeyValuePair<string, Func<object, UnityEngine.Object>>> _vmCars = new List<KeyValuePair<string, Func<object, UnityEngine.Object>>>();
        bool _ok, _hooks;

        sealed class LocalCar
        {
            public ushort Id; public byte Kind; public string Field; public Component Car; public GameObject Go; public Transform T; public Rigidbody Rb; public bool Unlocked;
            public readonly VehicleSendPolicy Policy = new VehicleSendPolicy();
            public readonly ParkPolicy Park = new ParkPolicy();
            public bool Mine, LastEngine, IsProxy, ExitRequested, FuelLoaded; public byte LastDriver;
            public RigidbodyConstraints SavedConstraints; public float Fuel = -1f;
            // Presentation on the ORIGINAL object (IsProxy = presented, not simulated here): AwayProxy = frozen hideout travel
            // access point; Observed (ParkedOther / RemoteVisual, M5.0) = kinematic, follows the holder's snapshots.
            public CarPresentation Pres = CarPresentation.Hidden;
            public bool Observed;
            public List<Collider> Blocked;                         // CarBox / van door / heavy-cargo colliders: no storage or cargo
            public readonly VehiclePoseBuffer Follow = new VehiclePoseBuffer();
            public double FollowRecv = -100;
            // M4.2.2
            public SeatSpec Seat;                                  // driver anchor in car-local space
            public readonly MotionLatch Latch = new MotionLatch(); // holder side: Moving flag
            public readonly RemoteMotion Remote = new RemoteMotion(); // observer side: last movement seen in snapshots
            public List<Collider> Solid; public bool Ghost;        // body colliders incl. doors; off only while the player stood inside on appearance
            public Bounds LocalBounds; public bool HasBounds;
            // M5.0 observed mode: saved rigidbody settings, paused EVP driving components, disabled wheel colliders, wheel visuals
            public bool SavedKinematic; public RigidbodyInterpolation SavedInterp; public CollisionDetectionMode SavedDetect;
            public List<Behaviour> Paused; public List<Collider> WheelCols; public bool SavedLoadTrunk;
            public WheelVis[] Wheels; public Vector3 WheelLastPos; public bool WheelHasPos; public float SteerTarget, Steer;
            // M5.0 doors: original Door components (car_door_L, car_door_R) and their replicated state
            public Component[] Doors; public bool DoorsBound; public readonly DoorReplica DoorRep = new DoorReplica();
            // M4.3 passenger seat: remote avatar anchor (original associateSitPos, Codename root) and the local first-person
            // player root (car-local), created on demand as a child of the car
            public SeatSpec PassengerSeat; public bool HasPassengerRoot; public Vec3 PassengerRootPos; public Quat PassengerRootRot; public Transform SeatAnchor;
            // M4.3.1: the original CarBox (its collider is disabled on a car simulated elsewhere, so the seat bridge aims at its
            // recorded box geometry) and the per-door slow-swing replay (start time, -1 = none) / own slow report in progress
            public Component CarBox; public BoxCollider CarBoxBox; public Bounds CarBoxLocal; public bool HasCarBox;
            public float GroundY;                                  // M4.3.2a car-local ground level (lowest wheel contact)
            public readonly double[] SlowAt = { -1, -1 }; public readonly bool[] SlowRep = new bool[2];
            public bool Active { get { return Go != null && Go.activeInHierarchy; } }
        }

        // One original wheel transform (EVP.Wheel.wheelTransform) in car-local terms: rest rotation, axle and steering axis of
        // its wheel collider, rolling radius.
        sealed class WheelVis { public Transform T; public Quaternion RestRel; public Vector3 AxleL, UpL; public float Radius, Spin; public bool Steers; }

        // Observer-side movement memory of one remote car (from accepted snapshots).
        sealed class RemoteMotion
        {
            public double LastActive = -100, LastMoved = -100; Vec3 _last; bool _has;
            public void Note(VehicleSnapshot s, double now)
            {
                bool moved = (_has && Vec3.Distance(s.Pos, _last) > 0.05f) || (s.Flags & VehicleStateFlags.Moving) != 0;
                if (moved || (s.Flags & VehicleStateFlags.Driven) != 0) LastActive = now;
                if (moved) LastMoved = now;
                _last = s.Pos; _has = true;
            }
            public bool Settled(double now) { return now - LastActive >= 1.0; }
            public bool Stopped(double now) { return now - LastMoved >= 1.0; }   // M4.3: a driver may sit inside
            public void Clear() { LastActive = -100; LastMoved = -100; _has = false; }
        }
        readonly List<LocalCar> _local = new List<LocalCar>();
        UnityEngine.Object _boundGm; double _nextBind;
        LocalCar _fuelCar;

        // ---- registry view ----
        readonly FleetView _view = new FleetView(); uint _viewRev = uint.MaxValue; bool _reconcile = true;
        uint _vehRev, _parkRev; bool _fleetDirty, _fleetUrgent;

        // ---- observed cars (simulated by the other instance, presented here on the original object) ----
        readonly VehicleSnapshotFilter _filter = new VehicleSnapshotFilter();
        readonly HashSet<Rigidbody> _observedBodies = new HashSet<Rigidbody>();
        readonly Dictionary<uint, ushort> _doorIds = new Dictionary<uint, ushort>();   // client: own door proposals in flight
        readonly HashSet<int> _warned = new HashSet<int>();
        int _region = Regions.None;
        CapsuleCollider _capsule; UnityEngine.Object _capsuleOwner;
        static readonly Collider[] _near = new Collider[24];

        // ---- host: client-held car ingest + persistence ----
        readonly Dictionary<ushort, ParkPolicy> _remotePark = new Dictionary<ushort, ParkPolicy>();
        readonly Dictionary<ushort, uint> _seqById = new Dictionary<ushort, uint>();   // survives Bind: receivers drop restarted sequences
        readonly FleetChangeTracker _changes = new FleetChangeTracker();
        Type _carBoxT, _vanDoorT, _heavyT, _doorT;
        FleetStore _store; FleetWriter _writer; int _fleetProfile = int.MinValue; bool _fleetReady; double _lastWrite = -100;

        // ---- travel ----
        readonly TravelGate _gate = new TravelGate();
        readonly OneShot _resume = new OneShot();
        object _pendingGm; MethodBase _pendingMethod;
        uint _hostTravelId;
        readonly HashSet<uint> _ownIds = new HashSet<uint>();   // claim / cancel proposals (results only logged)
        sealed class Arrival { public int Dest; public TravelOutcome Outcome; public ushort Car; public double At; public bool Offline; public bool Recovery; }
        Arrival _ctx;                      // set when a travel resumes; consumed by the next arrival in Dest
        int _initWanted = -1;              // carNr InitiateMainCar may activate (-1 none, int.MinValue = hideout: untouched)
        bool _placed; double _regionAt; string _travelStatus = "-";
        // M5 hotfix: fall guard (last grounded position per region, bounded recoveries) + arrival diagnostics
        Vector3 _safeFeet; int _safeRegion = Regions.None; double _airSince = -1, _nextFallCheck, _lastFallRecovery = -100; int _fallRecoveriesHere; bool _fallGaveUp;
        long _fallRecoveries, _loadPlacements;

        // ---- M4.2.2 driver handoff (requester side) ----
        sealed class Take { public ushort Id; public LocalCar Car; public uint ProposalId; public double At; public bool Granted; }
        Take _take;
        LocalCar _enterResume;            // the single original CarController.EnterVehicle call let through after a grant
        public const double TakeTimeout = 6.0;
        long _takeRequests, _takeGranted, _takeDenied, _takeEntered, _seatPosed, _solidSwitches;
        string _seatState = "-";

        // ---- diagnostics ----
        long _hintShows, _rideFootTicks, _rideTicksFrame, _quiet, _exitBlocked, _canInteractKept, _slowReports, _slowApplies, _fastFinishes, _footEntries, _storageOpens, _storageRefused, _medkitRefused;
        long _sent, _sentBytes, _recv, _recvBytes, _accepted, _stale, _unknown, _notOwner, _wrongRegion, _tooMany, _malformed, _commits, _observeEnter, _pushOuts, _doorReports, _doorApplies, _doorRefused;
        long _proxyMenus, _activations, _deactivations, _travels, _held, _resumed, _denied, _cancelled, _phoneBlocked, _placements, _placeFallbacks, _fleetWrites;
        long _costTicks, _costMax, _frames; double _nextStats, _nextStatus, _nextSlowLog, _nextRayLog; string _status = "-";

        public VehicleSync(SessionNode node, InstanceLog log, WorldSync world, PlayerSync players, string dataDir, VehicleSettings cfg)
        {
            _node = node; _log = log; _world = world; _players = players; _cfg = cfg ?? new VehicleSettings(); _dataDir = dataDir;
            _host = node.Role == Role.Host;
            _me = world.LocalSlot; _other = world.RemoteSlot;
            ResolveApi();
            node.MessageReceived += OnMessage;
            node.RemoteDisconnected += OnRemoteDisconnected;
            node.RemoteConnected += OnRemoteConnected;
            if (_ok)
            {
                string err;
                try { err = TravelHooks.Install("com.ts2mp.multiplayer.travel"); } catch (Exception ex) { err = ex.Message; }
                _hooks = err == null;
                if (_hooks) { TravelHooks.Handler = this; TravelHooks.Log = delegate(string m) { _log.Info(m); }; }
                else _log.Error("TRAVEL hooks not installed (" + err + "): travel stays original, region car rule NOT enforced");
            }
            world.ProposalResultHook = OnProposalResult;
            SleepPresenter.PassengerSeated = () => _ride != null && _ride.Stage == RideStage.Seated;   // M4.3.3 sleep presentation context
            if (_host && world.Host != null) world.Host.LivePark = LivePark;
            _log.Info("VEHICLE_INIT role=" + node.Role + " api=" + (_ok ? "ok" : "MISSING") + " travelHooks=" + (_hooks ? TravelHooks.Patched + " methods" : "off")
                + " carFields=" + string.Join(",", _vmCars.Select(k => k.Key).ToArray()) + " travelMenuKey=" + _cfg.TravelMenuKey
                + " jointTravel=" + (_hooks ? TravelHooks.SelectByDest.Count + " destinations, passengerMenuGuard=" + (TravelHooks.TownSelectionPatched ? "on" : "OFF") : "off"));
        }

        double Now { get { return _clock.Elapsed.TotalSeconds; } }

        void ResolveApi()
        {
            try
            {
                var gm = AccessTools.TypeByName("GameManager");
                var vm = AccessTools.TypeByName("VehiclesManager");
                var car = AccessTools.TypeByName("CarController");
                var ps = AccessTools.TypeByName("PlayerScript");
                var sg = AccessTools.TypeByName("SaveGame");
                var ws = AccessTools.TypeByName("WarningTextSpawner");
                var sp = AccessTools.TypeByName("SPrefs");
                _gmVM = Fast.Getter<UnityEngine.Object>(gm, "VM");
                _gmPS = Fast.Getter<UnityEngine.Object>(gm, "PS");
                _gmCar = Fast.Getter<UnityEngine.Object>(gm, "Car");
                _setGmCar = Fast.Setter<GameObject>(gm, "Car");
                _setGmTrunk = Fast.Setter<Transform>(gm, "trunkTransform");
                _gmTownSel = Fast.Getter<UnityEngine.Object>(gm, "TownSelectObj");
                _gmLoaded = Fast.Getter<bool>(gm, "loadedMap");
                _gmLeave = Fast.Getter<GameObject[]>(gm, "leavePoints");
                _gmDay = Fast.Getter<int>(gm, "currentDayInt");
                _gmWeek = Fast.Getter<int>(gm, "currentWeek");
                _psVehicle = Fast.Getter<UnityEngine.Object>(ps, "current_vehicle");
                _psTownSel = Fast.Getter<UnityEngine.Object>(ps, "TownSelection");
                _psSave = Fast.Getter<UnityEngine.Object>(ps, "saveGameScript");
                _psWarn = Fast.Getter<UnityEngine.Object>(ps, "warningSpawner");
                _psCC = Fast.Getter<UnityEngine.Object>(ps, "CC");
                _psChase = Fast.Getter<bool>(ps, "isPoliceChase");
                _psInVehicle = Fast.Getter<bool>(ps, "is_in_vehicle");
                _openTravel = Fast.Call0(ps, "OpenTownSelectionNoDelay");
                _psOpened = Fast.Getter<bool>(ps, "openedTownSelection");
                _setPsOpened = Fast.Setter<bool>(ps, "openedTownSelection");
                _psCaught = Fast.Getter<bool>(ps, "caughtUp");
                var stm = AccessTools.Method(ps, "ShowTownSelection", new[] { typeof(float) });
                if (stm != null && stm.ReturnType == typeof(System.Collections.IEnumerator))
                {
                    var po = System.Linq.Expressions.Expression.Parameter(typeof(object), "o"); var pf = System.Linq.Expressions.Expression.Parameter(typeof(float), "d");
                    _showTown = System.Linq.Expressions.Expression.Lambda<Func<object, float, System.Collections.IEnumerator>>(
                        System.Linq.Expressions.Expression.Call(System.Linq.Expressions.Expression.Convert(po, ps), stm, pf), po, pf).Compile();
                }
                _gmHideoutCar = Fast.Getter<UnityEngine.Object>(gm, "SpawnHideoutCar");
                _vmSecondPos = Fast.Getter<UnityEngine.Object>(vm, "secondCarHideoutPos");
                _carNr = Fast.Getter<int>(car, "carNr");
                _sitting = Fast.Getter<bool>(car, "is_sitting_inside");
                _engine = Fast.Getter<bool>(car, "engine_running");
                _isPlayerCar = Fast.Getter<bool>(car, "is_player_car");
                _carTrunk = Fast.Getter<Transform>(car, "trunkTransform");
                _startVoid = Fast.Call0(car, "StartVoid");
                _exitNow = Fast.Call0(car, "ExitCarImmidiately");
                _gas = Fast.Getter<float>(sg, "gasTankLeft");
                _setGas = Fast.Setter<float>(sg, "gasTankLeft");
                _warn = Fast.Call<string>(ws, "SpawnWarning");
                _vanPref = Fast.Getter<string>(vm, "vanBoughtPref");
                foreach (var f in AccessTools.GetDeclaredFields(vm))
                    if (!f.IsStatic && f.FieldType == car) _vmCars.Add(new KeyValuePair<string, Func<object, UnityEngine.Object>>(f.Name, Fast.Getter<UnityEngine.Object>(vm, f.Name)));
                _tppChar = Fast.Getter<UnityEngine.Object>(car, "tpp_character");
                _sitT = Fast.Getter<UnityEngine.Object>(car, "sit_transform");
                _assocT = Fast.Getter<UnityEngine.Object>(car, "associateSitPos");
                _enterVehicle = Fast.Call0(car, "EnterVehicle");
                _psMask = Fast.Getter<LayerMask>(ps, "mask");
                _psCam = Fast.Getter<UnityEngine.Object>(ps, "cam");
                _carDoorL = Fast.Getter<UnityEngine.Object>(car, "car_door_L");
                _carDoorR = Fast.Getter<UnityEngine.Object>(car, "car_door_R");
                _drivetrain = Fast.Getter<UnityEngine.Object>(car, "drivetrain");
                _loadTrunk = Fast.Getter<bool>(car, "load_trunk_items"); _setLoadTrunk = Fast.Setter<bool>(car, "load_trunk_items");
                _carMainCam = Fast.Getter<UnityEngine.Object>(car, "main_camera"); _carTppCam = Fast.Getter<UnityEngine.Object>(car, "tpp_camera");
                _carLeave = Fast.Getter<UnityEngine.Object>(car, "leave_car_transform");
                var tppT = AccessTools.TypeByName("TPP_Camera_Controller");
                _tppSetNewCar = Fast.Call<Transform>(tppT, "SetNewCar"); _tppDisable = Fast.Call0(tppT, "DisableTPP"); _tppCamObj = Fast.Getter<UnityEngine.Object>(tppT, "camObj");
                _gmMinimap = Fast.Getter<UnityEngine.Object>(gm, "minimapScript");
                _mmSetTarget = Fast.Call<GameObject>(AccessTools.TypeByName("bl_MiniMap"), "SetTarget");
                _psPCB = Fast.Getter<UnityEngine.Object>(ps, "PCB"); _psHand = Fast.Getter<UnityEngine.Object>(ps, "HandCamera"); _psPMB = Fast.Getter<UnityEngine.Object>(ps, "PMB");
                _psCamAnim = Fast.Getter<UnityEngine.Object>(ps, "CamAnim"); _psLight = Fast.Getter<UnityEngine.Object>(ps, "LightObj");
                _psTorch = Fast.Getter<bool>(ps, "torch"); _setTorch = Fast.Setter<bool>(ps, "torch");
                _setCanInteract = Fast.Setter<bool>(ps, "canInteract"); _interactDelayed = Fast.Call<float>(ps, "InteractDelayed"); _hideWeapons = Fast.Call0(ps, "HideWeapons");
                var pmbT = AccessTools.TypeByName("PlayerMotorBehavior"); var pcbT = AccessTools.TypeByName("PlayerCameraBehavior");
                _pmbSetVelY = Fast.Setter<float>(pmbT, "velocityY");
                _pmbSetCanMove = Fast.Setter<bool>(pmbT, "canMove"); _pmbSetCrouch = Fast.Setter<bool>(pmbT, "isCrouching"); _pmbHeight = Fast.Call0(pmbT, "SetControllerHeightToDefault");
                _pcbEnterRot = Fast.Call<float>(pcbT, "CarEnterRot"); _pcbExitRot = Fast.Call<float>(pcbT, "CarExitRot");
                _psCanInteract = Fast.Getter<bool>(ps, "canInteract"); _psCarrying = Fast.Getter<bool>(ps, "isCarrying"); _psHiding = Fast.Getter<bool>(ps, "isHiding");
                _psSlowDoor = Fast.Getter<bool>(ps, "opening_slowly"); _psCloseUp = Fast.Getter<bool>(ps, "closeUp"); _psHideHands = Fast.Getter<bool>(ps, "hideHands");
                _pmbRunning = Fast.Getter<bool>(pmbT, "isRunning");
                _psEnterInfo = Fast.Getter<UnityEngine.Object>(ps, "CarEnterInfo"); _psInv = Fast.Getter<UnityEngine.Object>(ps, "invScript");
                _psPrompts = Fast.Getter<UnityEngine.Object>(ps, "alphaInteractPrompts");
                foreach (var n in new[] { "universalInteractPrompt", "UsePC", "InteractInfo", "DoorOpenInfo", "relockPrompt", "use_keyfob_prompt", "need_signal_prompt",
                    "lockpick_lockpick_prompts", "lockpick_blt_prompts", "lockpick_automatic_prompts", "drillDoorInfo", "RunCarInfo", "weldPrompt", "hack_PDA_prompt",
                    "door_key_prompt_2", "CablesInfo", "BreakinCrowbarPrompt", "BreakinCrowbarPrompt2", "retrieve_camera_prompt", "pick_brick_prompt", "parcelPrompt",
                    "micro_camera_prompt", "interact_Shop_prompt" })
                { var g = Fast.Getter<UnityEngine.Object>(ps, n); if (g != null) _staleTargetPrompts.Add(g); }
                _psOpenStorage = Fast.Call<int>(ps, "OpenStorage");
                var invF = AccessTools.Field(ps, "invScript");
                if (invF != null) { _invShop = Fast.Getter<bool>(invF.FieldType, "isShop"); _invStorage = Fast.Getter<bool>(invF.FieldType, "isStorage"); }
                _medkitT = AccessTools.TypeByName("Medkit");
                var rwf = AccessTools.Field(ps, "rewiredPlayer");
                var gbd = rwf != null ? AccessTools.Method(rwf.FieldType, "GetButtonDown", new[] { typeof(string) }) : null;
                if (gbd != null && gbd.ReturnType == typeof(bool))
                {
                    var po3 = System.Linq.Expressions.Expression.Parameter(typeof(object), "o");
                    _interact = System.Linq.Expressions.Expression.Lambda<Func<object, bool>>(System.Linq.Expressions.Expression.Call(
                        System.Linq.Expressions.Expression.Field(System.Linq.Expressions.Expression.Convert(po3, ps), rwf), gbd, System.Linq.Expressions.Expression.Constant("interact")), po3).Compile();
                }
                // M4.2.2: every type lookup happens here (startup), never during gameplay. AccessTools.TypeByName scans all
                // loaded assemblies; the M4.2.1 logs showed ~45 ms (first hideout proxy) and ~113 ms (first arrival
                // placement) single-frame spikes from these lazy lookups.
                _doorT = AccessTools.TypeByName("Door"); _carBoxT = AccessTools.TypeByName("CarBox"); _vanDoorT = AccessTools.TypeByName("VanDoorTrigger"); _heavyT = AccessTools.TypeByName("Place_Heavy_Trigger");
                if (_doorT != null)
                {
                    _doorIsOpen = Fast.Getter<bool>(_doorT, "isOpen"); _doorSetOpen = Fast.Setter<bool>(_doorT, "isOpen");
                    _doorSetOpening = Fast.Setter<bool>(_doorT, "opening"); _doorSetByPlayer = Fast.Setter<bool>(_doorT, "openedByPlayer");
                    _doorSetCanUse = Fast.Setter<bool>(_doorT, "canUse"); _doorSetSpeed = Fast.Setter<float>(_doorT, "openSpeed");
                    _doorSpeedMul = Fast.Getter<float>(_doorT, "speedMultiplier"); _doorVolume = Fast.Getter<float>(_doorT, "maxVolume");
                    _doorSound = Fast.Getter<UnityEngine.Object>(_doorT, "DoorSound");
                    _doorClipOpen = Fast.Getter<UnityEngine.Object>(_doorT, "DoorOpen"); _doorClipClose = Fast.Getter<UnityEngine.Object>(_doorT, "DoorClose");
                    _doorLoadSlow = Fast.Getter<bool>(_doorT, "load_slow"); _doorSlowly = Fast.Getter<bool>(_doorT, "openingSlowly");
                }
                var evp = AccessTools.TypeByName("EVP.VehicleController");
                if (evp != null)
                {
                    var sa = AccessTools.Property(evp, "steerAngle");
                    if (sa != null && sa.PropertyType == typeof(float) && sa.GetGetMethod(true) != null)
                    {
                        var po2 = System.Linq.Expressions.Expression.Parameter(typeof(object), "o");
                        _evpSteer = System.Linq.Expressions.Expression.Lambda<Func<object, float>>(
                            System.Linq.Expressions.Expression.Property(System.Linq.Expressions.Expression.Convert(po2, evp), sa), po2).Compile();
                    }
                    _evpWheels = AccessTools.Field(evp, "wheels");
                    var wt = AccessTools.TypeByName("EVP.Wheel");
                    if (wt != null) { _wCol = AccessTools.Field(wt, "wheelCollider"); _wT = AccessTools.Field(wt, "wheelTransform"); _wSteer = AccessTools.Field(wt, "steer"); }
                }
                if (_doorT != null)
                {
                    _doorSpeedNow = Fast.Getter<float>(_doorT, "openSpeed"); _doorSlowMul = Fast.Getter<float>(_doorT, "slowOpenSpeedMulti");
                    _doorOpening = Fast.Getter<bool>(_doorT, "opening"); _doorSqueak = Fast.Getter<UnityEngine.Object>(_doorT, "DoorSqueak");
                    _doorSetSpeed2 = Fast.Setter<float>(_doorT, "openSpeed2");
                }
                if (_carBoxT != null)
                {
                    _carBoxType = Fast.Getter<int>(_carBoxT, "storage_type"); _carBoxHl = Fast.Getter<UnityEngine.Object>(_carBoxT, "highlightt");
                    var hlF = AccessTools.Field(_carBoxT, "highlightt");
                    var hlP = hlF != null ? AccessTools.Property(hlF.FieldType, "highlighted") : null;
                    if (hlP != null && hlP.PropertyType == typeof(bool) && hlP.GetSetMethod(true) != null)
                    {
                        var ph = System.Linq.Expressions.Expression.Parameter(typeof(object), "o"); var pv = System.Linq.Expressions.Expression.Parameter(typeof(bool), "v");
                        _hlSet = System.Linq.Expressions.Expression.Lambda<Action<object, bool>>(System.Linq.Expressions.Expression.Call(
                            System.Linq.Expressions.Expression.Convert(ph, hlF.FieldType), hlP.GetSetMethod(true), pv), ph, pv).Compile();
                    }
                }
                _trafficType = AccessTools.TypeByName("TurnTheGameOn.SimpleTrafficSystem.AITrafficCar");
                ResolveAstar();
                var gi = AccessTools.Method(sp, "GetInt", new[] { typeof(string) });
                var gf = AccessTools.Method(sp, "GetFloat", new[] { typeof(string) });
                var si = AccessTools.Method(sp, "SetInt", new[] { typeof(string), typeof(int) });
                if (gi != null && gi.ReturnType == typeof(int)) _prefInt = (Func<string, int>)Delegate.CreateDelegate(typeof(Func<string, int>), gi);
                if (gf != null && gf.ReturnType == typeof(float)) _prefFloat = (Func<string, float>)Delegate.CreateDelegate(typeof(Func<string, float>), gf);
                if (si != null && si.ReturnType == typeof(void)) _prefSetInt = (Action<string, int>)Delegate.CreateDelegate(typeof(Action<string, int>), si);
            }
            catch (Exception ex) { _log.Error("VEHICLE accessor compilation failed", ex); }
            _ok = _gmVM != null && _gmPS != null && _gmCar != null && _setGmCar != null && _setGmTrunk != null && _gmTownSel != null && _gmLoaded != null && _gmLeave != null
                && _psVehicle != null && _psTownSel != null && _psSave != null && _psWarn != null && _psCC != null && _psChase != null && _psInVehicle != null && _openTravel != null
                && _carNr != null && _sitting != null && _engine != null && _isPlayerCar != null && _carTrunk != null && _startVoid != null && _exitNow != null
                && _gas != null && _setGas != null && _warn != null && _prefInt != null && _prefFloat != null && _prefSetInt != null && _vmCars.Count > 0
                && _gmDay != null && _gmWeek != null && _psOpened != null && _setPsOpened != null && _psCaught != null && _showTown != null;
            if (!_ok) _log.Error("VEHICLE game API incomplete: vehicle sync and travel coordination disabled");
            if (_ok && (_enterVehicle == null || _sitT == null)) _log.Warn("VEHICLE handoff entry/seat accessors missing: driver handoff disabled");
            if (_ok && !DoorApi) _log.Warn("VEHICLE Door/car_door accessors missing: car door state is not synchronized");
            if (_ok && !PassengerApi) _log.Warn("VEHICLE passenger accessors missing: passenger seats disabled");
            if (_ok && PassengerApi && !ResolverApi) _log.Warn("VEHICLE interaction resolver accessors missing: passenger entry only through an open door, seat storage disabled");
            if (_ok && (_carBoxType == null || _psOpenStorage == null)) _log.Warn("VEHICLE CarBox/OpenStorage accessors missing: storage from the passenger seat disabled");
            if (_ok && DoorApi && !SlowDoorApi) _log.Warn("VEHICLE slow-door accessors missing: slow door movements are shown at normal speed");
            if (_ok && (_evpSteer == null || _evpWheels == null || _wT == null || _wCol == null)) _log.Warn("VEHICLE EVP steer/wheel accessors missing: remote wheels do not turn");
        }

        int Pref(string k) { try { return _prefInt(k); } catch { return 0; } }
        float PrefF(string k) { try { return _prefFloat(k); } catch { return -1f; } }
        static string CarName(int carNr) { return carNr == 0 ? "Ranger" : carNr == 1 ? "Shadowhawk" : "car" + carNr; }
        static string CarName(ushort id) { return id == VehicleIds.None ? "-" : CarName(VehicleIds.ToCarNr(id)); }

        // ======================================================================== per frame
        public void Update()
        {
            if (!_ok) return;
            long t0 = Stopwatch.GetTimestamp();
            double now = Now;
            var gm = _world.GameManagerObject;
            if (gm == null) { if (_local.Count > 0) { _local.Clear(); _boundGm = null; _fuelCar = null; } }
            else if (!ReferenceEquals(gm, _boundGm) && now >= _nextBind) Bind(gm, now);
            int region = _world.LocalRegion;
            if (region != _region) OnRegionChanged(region, now);
            EnsureView();
            JointFrame(now, gm, region);   // M4.3 Phase B: before Reconcile (a held passenger's car must keep its presentation)
            if (_host) HostFleetFrame(now);
            else if (_gate.CheckTimeout(now))
            {
                _denied++; _pendingGm = null; _pendingMethod = null; _travelStatus = "host did not answer";
                _log.Warn("TRAVEL request timed out after " + _gate.ResultTimeout + " s: travel menu released (try again)");
                Quiet("Multiplayer: the host did not answer, try again");
            }

            long t1 = Stopwatch.GetTimestamp(), t2 = t1, t3 = t1;
            bool footRan = false;
            if (_take != null && !_take.Granted && now - _take.At > TakeTimeout) EndTake("the host did not answer", true);
            if (gm != null && _local.Count > 0 && region != Regions.None)
            {
                var ps = _gmPS(gm);
                if (_gmLoaded(gm) && TravelRules.IsFleetRegion(region))
                {
                    if (_reconcile || NeedsExitRetry()) { _reconcile = false; Reconcile(gm, ps, region); }
                    t2 = Stopwatch.GetTimestamp();
                    FuelFrame(ps);
                    if (_take != null && _take.Granted) EnterFrame(now, ps);
                    if (_ride != null && _jStage != JStage.Departing) { long r0 = Stopwatch.GetTimestamp(); RideFrame(now, ps, region); _rideTicksFrame = Stopwatch.GetTimestamp() - r0; }
                    long tr0 = Stopwatch.GetTimestamp();
                    if (_ride == null && _take == null && _jStage == JStage.None) { FootFrame(now, ps); footRan = true; }
                    _rideFootTicks += Stopwatch.GetTimestamp() - tr0 + _rideTicksFrame; _rideTicksFrame = 0;
                    if (!_placed && ps != null && !JointWaiting) TryPlace(now, gm, ps, region);
                    if (_placed && ps != null && _ride == null && _take == null && _jStage == JStage.None) FallGuard(now, gm, ps, region);
                    t3 = Stopwatch.GetTimestamp();
                    if (Input.GetKeyDown(_cfg.TravelMenuKey)) TravelMenuKey(ps, region);
                }
                StreamFrame(now, ps, region);
            }
            if (!footRan) _entryHint = false;   // seated, requesting, scene load, travel, other region, disconnected: no prompt
            CollisionFrame(now, gm);
            PushOutFrame(gm);
            WheelFrame();
            DoorFrame(region);
            SeatFrame();
            if (_cfg.InteractDebug && _observedBodies.Count > 0 && now >= _nextRayLog && Input.GetKeyDown(KeyCode.E)) { _nextRayLog = now + 1; RayProbe(gm); }

            long t4 = Stopwatch.GetTimestamp();
            long cost = t4 - t0;
            _costTicks += cost; _frames++; if (cost > _costMax) _costMax = cost;
            if (cost * 1000.0 / Stopwatch.Frequency > 20.0 && now >= _nextSlowLog)
            {
                // Measured breakdown of a rare long frame of OUR code (bind/view, reconcile, fuel/entry/placement, rest).
                _nextSlowLog = now + 5;
                double f = 1000.0 / Stopwatch.Frequency;
                _log.Info("VEHICLE_SLOW_FRAME " + (cost * f).ToString("0.0") + " ms: bind+view=" + ((t1 - t0) * f).ToString("0.0") + " reconcile=" + ((t2 - t1) * f).ToString("0.0")
                    + " fuel+entry+placement=" + ((t3 - t2) * f).ToString("0.0") + " stream+collision+pushout+wheels+doors+seat=" + ((t4 - t3) * f).ToString("0.0"));
            }
            if (now >= _nextStatus) { _nextStatus = now + 0.5; BuildStatus(); }
            if (now >= _nextStats) { _nextStats = now + 30; LogStats(); }
        }

        // M5.0: observed cars are kinematic bodies moved to the holder's interpolated pose in the physics step, so contacts with
        // the local CharacterController and traffic see a moving solid body; Rigidbody interpolation renders it smoothly.
        // Large jumps (snap in the pose buffer, handoff, travel) teleport instead of sweeping through the world.
        public void FixedStep()
        {
            if (!_ok || _observedBodies.Count == 0) return;
            double now = Now;
            for (int i = 0; i < _local.Count; i++)
            {
                var lc = _local[i];
                if (!lc.Observed || lc.Rb == null || lc.Follow.Count == 0 || !lc.Active) continue;
                VehiclePose pose;
                if (!lc.Follow.Sample(now, out pose)) continue;
                var pos = new Vector3(pose.Pos.X, pose.Pos.Y, pose.Pos.Z);
                var rot = new Quaternion(pose.Rot.X, pose.Rot.Y, pose.Rot.Z, pose.Rot.W);
                if ((lc.Rb.position - pos).sqrMagnitude > 25f) { lc.Rb.position = pos; lc.Rb.rotation = rot; lc.T.SetPositionAndRotation(pos, rot); lc.WheelHasPos = false; }
                else { lc.Rb.MovePosition(pos); lc.Rb.MoveRotation(rot); }
            }
        }

        // M4.3 Phase B: the departing joint passenger's scene is being replaced - the seated player object goes with it; the
        // seat stays assigned on the host (no exit, no placement, no release).
        bool JointDropRide(string why)
        {
            if (_jStage != JStage.Departing || _ride == null) return false;
            _ride = null; HideSelfBody();
            _log.Info("JOINT trip#" + _jId + " passenger scene unloading (" + why + "): seat kept on the host");
            return true;
        }

        void OnRegionChanged(int region, double now)
        {
            if (_jStage == JStage.Departing && region != _region)
            {
                JointDropRide("region " + Regions.Name(_region) + " -> " + Regions.Name(region));
                _jStage = JStage.Waiting; _jFrozenPs = null;
            }
            if (_jStage == JStage.Waiting && _hasTrip && region == _trip.Dest)
            {
                _jArriveAt = now;
                double load = now - _jAt;
                if (load > _jLoadMax) _jLoadMax = load;
                _log.Info("JOINT trip#" + _jId + " passenger arrived in " + Regions.Name(region) + " " + load.ToString("0.0") + " s after departure (waiting for the car to be live here)");
            }
            if (_jStage == JStage.Holding && region != _region) { _jStage = JStage.None; _jCar = 0; }
            if (_ride != null) ForceExitRide("region changed", false);
            foreach (var lc in _local) if (lc.Observed) ExitObserved(lc);
            _observedBodies.Clear(); _filter.Clear();
            _region = region; _regionAt = now; _placed = false; _reconcile = true;
            _fallRecoveriesHere = 0; _fallGaveUp = false; _airSince = -1;   // _safeFeet is only used while _safeRegion == region
            foreach (var lc in _local) { lc.Mine = false; lc.FuelLoaded = false; lc.Policy.Reset(); lc.Park.Reset(); lc.Follow.Clear(); lc.FollowRecv = -100; lc.Latch.Reset(); lc.Remote.Clear(); }
            _fuelCar = null;
            if (_take != null) EndTake("region changed", false);
            _enterResume = null;
            if (_ctx != null && region != Regions.None && region != _ctx.Dest) { _log.Warn("ARRIVAL expected " + Regions.Name(_ctx.Dest) + " but arrived in " + Regions.Name(region) + " (context dropped)"); _ctx = null; }
            if (_gate.Pending && region != Regions.None) AbandonPending("region changed");
        }

        // Once per GameManager instance (every scene load): discover the cars on VehiclesManager.
        void Bind(UnityEngine.Object gm, double now)
        {
            JointDropRide("scene reloaded");
            if (_ride != null) ForceExitRide("scene reloaded", false);
            _local.Clear(); _fuelCar = null; _observedBodies.Clear();
            var vm = _gmVM(gm);
            if (vm == null) { _nextBind = now + 1; return; }   // GameManager.Start not done yet
            _boundGm = gm;
            string vanPref = _vanPref != null ? _vanPref(vm) : null;
            var parts = new List<string>();
            foreach (var kv in _vmCars)
            {
                var car = kv.Value(vm) as Component;
                if (car == null) continue;
                int nr = _carNr(car);
                ushort id = VehicleIds.FromCarNr(nr);
                if (id == VehicleIds.None) { _log.Warn("VEHICLE " + kv.Key + " has invalid carNr " + nr + " (ignored)"); continue; }
                if (_local.Any(l => l.Id == id)) { _log.Error("VEHICLE duplicate identity carNr " + nr + " on field " + kv.Key + " (ignored)"); continue; }
                // Verified unlock rule (VehiclesManager.InitiateMainCar / Tutorial.BuyVan): 'van' needs SPrefs vanBoughtPref == 1.
                bool unlocked = kv.Key != "van" || (vanPref != null && Pref(vanPref) == 1);
                var lc = new LocalCar { Id = id, Kind = (byte)nr, Field = kv.Key, Car = car, Go = car.gameObject, T = car.transform, Rb = car.GetComponent<Rigidbody>(), Unlocked = unlocked };
                _local.Add(lc);
                parts.Add(kv.Key + ":" + car.gameObject.name + "#" + id + (unlocked ? "" : "(locked)") + (lc.Active ? "(active)" : ""));
            }
            ProbeSeats();
            if (_host) EnsureHostFleet(vm);
            _reconcile = true;
            _log.Info("VEHICLE_BIND " + string.Join(" ", parts.ToArray()));
        }

        // ======================================================================== registry view / persistence
        void EnsureView()
        {
            var reg = _world.Registry;
            if (reg.HeadRev == _viewRev) return;
            _viewRev = reg.HeadRev;
            _view.Refresh(reg);
            ReadTrip(reg);
            _reconcile = true;
            foreach (var lc in _local)
            {
                var de = reg.Get(StateScope.Session, lc.Id, WorldKeys.VehicleDoor);
                byte dm; if (de == null || !VehicleDoors.TryParseState(de.Value, out dm)) dm = 0;   // never committed: closed (fresh scene doors)
                lc.DoorRep.OnCommitted(dm);
            }
            var changed = _changes.Changed(_view.All);
            if (changed != null)
                foreach (var id in changed)
                {
                    _filter.Forget(id);
                    foreach (var lc in _local) if (lc.Id == id) { lc.Follow.Clear(); lc.FollowRecv = -100; }
                }
            if (!_host) return;
            uint vr = 0, pr = 0;
            foreach (var e in reg.Entries)
            {
                if (e.Scope != StateScope.Session) continue;
                if (e.Key == WorldKeys.Vehicle && e.Rev > vr) vr = e.Rev;
                else if (e.Key == WorldKeys.VehiclePark && e.Rev > pr) pr = e.Rev;
            }
            if (vr != _vehRev) { _vehRev = vr; _fleetDirty = true; _fleetUrgent = true; }
            if (pr != _parkRev) { _parkRev = pr; _fleetDirty = true; }
        }

        bool Rec(ushort id, out VehicleRecord r) { return _view.TryGet(id, out r); }

        // Host: register the cars of this save and restore the fleet record of this profile (once per profile).
        void EnsureHostFleet(object vm)
        {
            if (!_host || vm == null) return;
            int profile = PlayerPrefs.GetInt("Profile");
            if (_fleetReady && profile == _fleetProfile) return;
            if (_fleetReady) { FlushFleet(true); _log.Warn("FLEET profile changed " + _fleetProfile + " -> " + profile); }
            var fleet = _world.Host.Fleet; var outbox = _world.Host.Outbox;
            string vanPref = _vanPref != null ? _vanPref(vm) : null;
            foreach (var kv in _vmCars)
            {
                var car = kv.Value(vm) as Component;
                if (car == null) continue;
                int nr = _carNr(car); ushort id = VehicleIds.FromCarNr(nr);
                if (id == VehicleIds.None) continue;
                bool unlocked = kv.Key != "van" || (vanPref != null && Pref(vanPref) == 1);
                if (fleet.Register(id, (byte)nr, unlocked, outbox) == ProposalCode.Invalid) _log.Error("VEHICLE register refused for #" + id + " (identity conflict)");
            }
            _fleetProfile = profile;
            _store = new FleetStore(_dataDir, profile);
            _writer = new FleetWriter(_store);
            int story = Pref("Story"), day = Pref("Week") * 7 + Pref("Day");
            FleetSave fs = null; string info;
            try
            {
                if (_store.TryLoad(out fs, out info))
                {
                    if (info != "loaded") _log.Warn("FLEET " + info);
                    string stale = fs.StaleReason(profile, story, day);
                    if (stale != null) { var q = _store.Quarantine("stale"); _log.Warn("FLEET record for profile " + profile + " ignored: " + stale + " (kept as " + q + ")"); fs = null; }
                }
                else if (info != "missing") { var q = _store.Quarantine("invalid"); _log.Warn("FLEET record for profile " + profile + " " + info + " (kept as " + q + ")"); }
            }
            catch (Exception ex) { _log.Error("FLEET load failed (starting from the save)", ex); fs = null; }
            var applied = new HashSet<ushort>();
            if (fs != null)
            {
                foreach (var c in fs.Cars)
                {
                    VehicleRecord r;
                    if (!fleet.TryGet(c.Id, out r) || r.Kind != c.Kind) { _log.Warn("FLEET entry #" + c.Id + " has no matching car in this save (ignored)"); continue; }
                    if (!fleet.Place(c.Id, c.Region, outbox)) { _log.Warn("FLEET entry #" + c.Id + " region " + Regions.Name(c.Region) + " refused (occupied)"); continue; }
                    fleet.SetPark(c.Id, c.Park, outbox);
                    applied.Add(c.Id);
                }
                _log.Info("FLEET_LOAD profile=" + profile + " " + string.Join(" ", fs.Cars.Select(c => CarName(c.Id) + "@" + Regions.Name(c.Region) + " " + c.Park).ToArray())
                    + " written=" + new DateTime(fs.SavedUtcTicks, DateTimeKind.Utc).ToString("u") + " file=" + _store.Path);
            }
            // Cars without a fleet record: the original rule (current car in the save's region, the other in the hideout).
            int cur = Pref("CurrentCar"), loc = Pref("CurrentLocation");
            float gas = PrefF("GasTankLeftCar");
            foreach (var id in fleet.Ids())
            {
                if (applied.Contains(id)) continue;
                VehicleRecord r; fleet.TryGet(id, out r);
                int reg = r.Kind == cur && r.Unlocked && TravelRules.IsFleetRegion(loc) ? loc : TravelRules.Hideout;
                if (!fleet.Place(id, reg, outbox)) { reg = TravelRules.Hideout; fleet.Place(id, reg, outbox); }
                fleet.SetPark(id, new ParkRecord { Region = reg, HasPose = false, Fuel = gas >= 0 && gas <= TravelRules.MaxFuel ? gas : -1f }, outbox);
                _log.Info("FLEET_INIT " + CarName(id) + " -> " + Regions.Name(reg) + " (no fleet record: from the host save CurrentCar=" + cur + " CurrentLocation=" + loc + ", fuel " + gas.ToString("0.0") + ")");
            }
            _fleetReady = true; _fleetDirty = true; _fleetUrgent = true;
            _world.FlushOutbox();
        }

        FleetSave ExportFleet()
        {
            var fleet = _world.Host.Fleet;
            var gm = _world.GameManagerObject;
            var fs = new FleetSave { Profile = _fleetProfile, Story = Pref("Story"), DayIndex = gm != null ? _gmWeek(gm) * 7 + _gmDay(gm) : Pref("Week") * 7 + Pref("Day"), SavedUtcTicks = DateTime.UtcNow.Ticks };
            foreach (var id in fleet.Ids())
            {
                VehicleRecord r; ParkRecord p;
                if (!fleet.TryGet(id, out r)) continue;
                if (!fleet.TryGetPark(id, out p)) p = new ParkRecord { Region = r.Region, Fuel = -1f };
                int committed = _world.Host.Travel.CommittedRegion(id, r.Region);
                if (p.Region != committed) { p.HasPose = false; p.Region = committed; }   // never persist a pose of an uncommitted destination
                fs.Cars.Add(new FleetSaveCar { Id = id, Kind = r.Kind, Unlocked = r.Unlocked, Region = committed, Park = p });
            }
            return fs;
        }

        void HostFleetFrame(double now)
        {
            if (!_fleetReady || !_fleetDirty) return;
            if (now - _lastWrite < (_fleetUrgent ? 1.0 : 10.0)) return;
            FlushFleet(false);
        }

        void FlushFleet(bool sync)
        {
            if (!_fleetReady || _writer == null) return;
            _fleetDirty = false; _fleetUrgent = false; _lastWrite = Now;
            try
            {
                var data = ExportFleet().Serialize();
                if (sync) { _writer.Flush(2000); _store.WriteAtomic(data); }
                else _writer.Queue(data);
                _fleetWrites++;
            }
            catch (Exception ex) { _log.Error("FLEET write failed", ex); }
        }

        // ======================================================================== travel (Harmony handlers)
        bool MenuOpen(object gm)
        {
            var a = _gmTownSel(gm) as GameObject;
            var ps = _gmPS(gm);
            var b = ps != null ? _psTownSel(ps) as GameObject : null;
            return (a != null && a.activeInHierarchy) || (b != null && b.activeInHierarchy);
        }

        public bool OnSelect(object gm, MethodBase method, int dest)
        {
            if (_resume.TryConsume(method)) { _resumed++; return true; }   // our own continuation: run the original once
            if (!MenuOpen(gm))
            {
                // Code-driven travel (arrest, heist start, stolen-car delivery, hideout repairs): original behaviour, no car
                // is carried; arrival follows the registry.
                _ctx = null;
                _log.Info("TRAVEL " + method.Name + " -> " + Regions.Name(dest) + " (scripted transition: original behaviour, no car carried)");
                return true;
            }
            string seatBlock = SeatTravelBlock();
            if (seatBlock != null)
            {
                _log.Info("TRAVEL " + method.Name + " -> " + Regions.Name(dest) + " refused locally: " + seatBlock);
                Quiet("Multiplayer: " + seatBlock);
                return false;   // travel menu stays open; nobody detaches or is stranded
            }
            int origin = _region;
            ushort car; ParkRecord pose;
            Candidate(gm, origin, out car, out pose);
            var req = new TravelRequest { Dest = dest, Car = car, Pose = pose };
            if (_host)
            {
                _travels++;
                string refused;
                var g = _world.Host.HostTravel(++_hostTravelId, req, _world.Clock, out refused);   // host clock: the same one WorldHost.Tick expires with
                if (refused != null)
                {
                    _denied++; _travelStatus = "refused (" + refused + ")";
                    _log.Info("TRAVEL " + method.Name + " -> " + Regions.Name(dest) + " refused by the host rules: " + refused + " (travel menu stays open)");
                    _world.DrainTravelNotes();
                    return false;
                }
                _world.FlushOutbox();
                SetContext(g, false);
                FlushFleet(false);
                return true;
            }
            if (!_world.Synced)
            {
                _travels++;
                _ctx = new Arrival { Dest = dest, Outcome = TravelOutcome.None, At = Now, Offline = true };
                _log.Warn("TRAVEL " + method.Name + " -> " + Regions.Name(dest) + " while not synchronized with the host: travelling on foot, no car");
                return true;
            }
            if (_gate.Pending) { _log.Info("TRAVEL click ignored (request #" + _gate.PendingId + " still waiting for the host)"); return false; }
            _travels++;
            var e = new StateEntry { Scope = StateScope.Player, ScopeArg = _me, Key = WorldKeys.Travel, Value = req.ToBytes() };
            uint id = _world.SendProposal(e);
            _gate.Begin(id, dest, Now);
            _pendingGm = gm; _pendingMethod = method; _held++;
            _travelStatus = "waiting for host (" + Regions.Name(dest) + ")";
            _log.Info("TRAVEL request #" + id + " " + method.Name + " -> " + Regions.Name(dest) + " from " + Regions.Name(origin) + " car=" + CarName(car) + (pose.HasPose ? " pose " + pose : "") + " (original travel held)");
            return false;
        }

        // The car this traveller would bring: the player car they sit in, else the car they hold in this region.
        void Candidate(object gm, int origin, out ushort car, out ParkRecord pose)
        {
            car = VehicleIds.None; pose = new ParkRecord { Region = origin, Fuel = -1f };
            var ps = _gmPS(gm);
            var cur = ps != null ? _psVehicle(ps) as Component : null;
            LocalCar pick = null;
            foreach (var lc in _local) if (cur != null && ReferenceEquals(lc.Car, cur) && _sitting(lc.Car)) pick = lc;
            if (pick == null && TravelRules.OneCarRegion(origin)) foreach (var lc in _local) if (lc.Mine && lc.Active) pick = lc;
            if (pick == null && origin == TravelRules.Hideout && cur != null) foreach (var lc in _local) if (ReferenceEquals(lc.Car, cur) && lc.Mine) pick = lc;
            if (pick == null) return;
            car = pick.Id;
            if (pick.Mine && pick.Active && !pick.IsProxy)
            {
                if (pick == _fuelCar && ps != null) pick.Fuel = ReadGas(ps);
                var p = pick.T.position; var r = pick.T.rotation;
                pose = new ParkRecord { Region = origin, HasPose = true, Pos = new Vec3(p.x, p.y, p.z), Rot = new Quat(r.x, r.y, r.z, r.w), Fuel = pick.Fuel >= 0 ? Math.Min(pick.Fuel, TravelRules.MaxFuel) : -1f };
            }
        }

        void SetContext(TravelGrant g, bool client)
        {
            _ctx = new Arrival { Dest = g.Dest, Outcome = g.Outcome, Car = g.Car, At = Now };
            _travelStatus = Regions.Name(g.Dest) + ": " + g.Outcome + (g.Car != 0 ? " " + CarName(g.Car) : "");
            _log.Info("TRAVEL grant " + g + (client ? " (host decision; resuming the original travel)" : " (decided in place by the host)"));
        }

        bool OnProposalResult(uint id, ProposalCode code)
        {
            if (!_host && _take != null && !_take.Granted && id == _take.ProposalId)
            {
                _log.Info("VEHICLE " + CarName(_take.Id) + " driver handoff #" + id + " -> " + code);
                TakeResult(code);
                return true;
            }
            if (!_host && _ride != null && id == _ride.ProposalId && _ride.Stage == RideStage.Requested)
            {
                _log.Info("PASSENGER " + CarName(_ride.Id) + " seat request #" + id + " -> " + code);
                RideResult(code, null);
                return true;
            }
            if (!_host && _ride != null && id == _ride.ConfirmId)
            {
                _ride.ConfirmId = 0;
                if (code == ProposalCode.NotAuthorized || code == ProposalCode.Invalid) ForceExitRide("the host no longer holds the seat for this player (" + code + ")", true);
                return true;
            }
            ushort dvid;
            if (_doorIds.TryGetValue(id, out dvid))
            {
                _doorIds.Remove(id);
                foreach (var lc in _local) if (lc.Id == dvid) lc.DoorRep.Result();
                if (code != ProposalCode.Accepted && code != ProposalCode.Unchanged)
                {
                    _doorRefused++;
                    _log.Info("DOOR " + CarName(dvid) + " change #" + id + " refused by the host: " + code + " (door returns to the committed state)");
                    if (code == ProposalCode.Busy) Quiet("Multiplayer: the car is moving");
                }
                return true;
            }
            if (_ownIds.Remove(id))
            {
                if (code != ProposalCode.Accepted && code != ProposalCode.Unchanged) _log.Warn("VEHICLE proposal #" + id + " -> " + code);
                return true;
            }
            var act = _gate.OnResult(id, code == ProposalCode.Accepted);
            switch (act)
            {
                case GateAction.Ignore: return false;
                case GateAction.CancelLate:
                    _log.Warn("TRAVEL late grant for abandoned request #" + id + ": cancelling the reservation");
                    SendCancel(id);
                    return true;
                case GateAction.Denied:
                    _denied++; _pendingGm = null; _pendingMethod = null; _travelStatus = "refused (" + code + ")";
                    _log.Warn("TRAVEL request #" + id + " refused by the host: " + code + " (travel menu stays open)");
                    Quiet("Multiplayer: travel refused by the host (" + code + ")");
                    return true;
            }
            // GateAction.Resume
            var gm = _pendingGm; var method = _pendingMethod; _pendingGm = null; _pendingMethod = null;
            var ge = _world.Registry.Get(StateScope.Player, _me, WorldKeys.TravelGrant);
            TravelGrant g;
            if (ge == null || !TravelGrant.TryParse(ge.Value, out g) || g.Id != id)
            {
                _denied++; _log.Error("TRAVEL request #" + id + " accepted but no matching grant in the registry: cancelling");
                SendCancel(id); return true;
            }
            var gmo = gm as UnityEngine.Object;
            if (gmo == null || method == null || !ReferenceEquals(gmo, _world.GameManagerObject) || !MenuOpen(gm))
            {
                _travelStatus = "cancelled (menu closed)";
                _log.Info("TRAVEL grant #" + id + " arrived after the travel menu was closed: cancelled");
                SendCancel(id); return true;
            }
            SetContext(g, true);
            _resume.Arm(method);
            try { method.Invoke(gm, null); }
            catch (Exception ex) { _log.Error("TRAVEL resume of " + method.Name + " failed", ex); _ctx = null; SendCancel(id); }
            finally { if (_resume.Armed) { _resume.Disarm(); _log.Error("TRAVEL continuation guard was not consumed (original not re-entered)"); } }
            return true;
        }

        void SendCancel(uint requestId)
        {
            if (!_node.IsConnected) return;
            _cancelled++;
            var e = new StateEntry { Scope = StateScope.Player, ScopeArg = _me, Key = WorldKeys.TravelCancel, Value = BitConverter.GetBytes(requestId) };
            _ownIds.Add(_world.SendProposal(e));
        }

        void AbandonPending(string why)
        {
            uint id = _gate.Abandon();
            _pendingGm = null; _pendingMethod = null;
            if (id == 0) return;
            _travelStatus = "abandoned (" + why + ")";
            _log.Warn("TRAVEL request #" + id + " abandoned (" + why + ")");
            SendCancel(id);
        }

        // ---- arrival: VehiclesManager.InitiateMainCar (inside GameManager.Start) ----
        public void BeforeInitiate(object vm)
        {
            _initWanted = -1;
            int loc = Pref("CurrentLocation");
            if (_host) EnsureHostFleet(vm);
            if (!TravelRules.IsFleetRegion(loc)) { if (_ctx != null && _ctx.Dest == loc) _log.Info("ARRIVAL " + Regions.Name(loc) + ": special destination, original car behaviour"); return; }
            if (_ctx != null && _ctx.Dest != loc) _ctx = null;
            EnsureView();
            if (loc == TravelRules.Hideout) { _initWanted = int.MinValue; return; }   // the garage: original activation; reconcile freezes/hides
            ushort want = VehicleIds.None; string why;
            if (_ctx != null)
            {
                if (_ctx.Outcome == TravelOutcome.Carry || _ctx.Outcome == TravelOutcome.Parked) want = _ctx.Car;
                why = "travel grant " + _ctx.Outcome;
            }
            else if (_world.Synced)
            {
                // No travel context (continue/load, scripted transition, reconnect): predict from the registry; the host's
                // holder assignment after the region is confirmed is authoritative (reconcile corrects a wrong guess).
                bool otherHere = RegionDirectory.RegionOf(_world.Registry, _other) == loc;
                foreach (var kv in _view.All)
                {
                    var r = kv.Value;
                    if (r.Region == loc && r.Unlocked && (r.Holder == _me || (r.Holder == 0 && !otherHere))) want = kv.Key;
                }
                why = "registry";
            }
            else why = "not synchronized with the host";
            if (want != VehicleIds.None)
            {
                _initWanted = VehicleIds.ToCarNr(want);
                _prefSetInt("CurrentCar", _initWanted);
            }
            _log.Info("ARRIVAL " + Regions.Name(loc) + ": activating " + (want != VehicleIds.None ? CarName(want) : "no car") + " (" + why + ")");
        }

        public void AfterInitiate(object vm)
        {
            if (_initWanted == int.MinValue) { _initWanted = -1; return; }   // hideout
            if (!TravelRules.IsFleetRegion(Pref("CurrentLocation"))) return;
            foreach (var kv in _vmCars)
            {
                var car = kv.Value(vm) as Component;
                if (car == null || _carNr(car) == _initWanted || !car.gameObject.activeSelf) continue;
                car.gameObject.SetActive(false);
                _deactivations++;
                _log.Info("ARRIVAL original activation of " + CarName(_carNr(car)) + " suppressed (not this player's car here)");
            }
        }

        public bool AllowStartingCar(object car)
        {
            var c = car as Component;
            bool allow = _ctx != null && _ctx.Outcome == TravelOutcome.Carry && c != null && c.gameObject.activeInHierarchy && VehicleIds.FromCarNr(_carNr(c)) == _ctx.Car;
            if (!allow) _log.Info("ARRIVAL driver seating skipped (" + (_ctx == null ? "no travel grant" : _ctx.Outcome.ToString()) + "): the player arrives on foot");
            if (!allow && _ctx == null)
            {
                // M5 hotfix (verified IL GameManager.Start IL_03f4-043a Madison / IL_049b-04d1 Hillville): with Story > 3 and
                // Saved_Pos == 0 (every travel save, SaveGameNewSystem IL_0156) IsStartingCar() is the ONLY placement of the player.
                // A load without a travel grant (caught screen Load = GameManager.LoadCheckpoint -> LoadLevel("MainScene"),
                // menu Continue, client restart) that skips the seating left the player at MainScene's default position,
                // which has no ground in these maps -> endless fall. Same checked on-foot placement as a Join arrival instead.
                int loc = Pref("CurrentLocation");
                ushort carId = _initWanted >= 0 ? VehicleIds.FromCarNr(_initWanted) : VehicleIds.None;
                _ctx = new Arrival { Dest = loc, Outcome = TravelOutcome.Join, Car = carId, At = Now, Recovery = true };
                var gm = _world.GameManagerObject; var ps = gm != null ? _gmPS(gm) as Component : null; var cc = ps != null ? _psCC(ps) as CharacterController : null;
                _log.Info("ARRIVAL_DIAG load without travel grant (" + (TravelHooks.LastLoadReason ?? "continue / restart / scripted") + "): region " + Regions.Name(loc)
                    + " Saved_Pos=" + Pref("Saved_Pos") + " Story=" + Pref("Story") + " player at " + (ps != null ? Fmt(ps.transform.position) : "?")
                    + " cc=" + (cc != null ? (cc.enabled ? "on" : "off") : "?") + " car=" + (carId != VehicleIds.None ? CarName(carId) : "none")
                    + ": checked on-foot placement (other player / region car / original leave point) once the region is ready");
                TravelHooks.LastLoadReason = null;
            }
            return allow;
        }

        public bool AllowSpawnAtCar(object ps)
        {
            if (_ride != null) { _phoneBlocked++; _log.Info("PHONE fast travel blocked: seated as passenger"); var w0 = _psWarn(ps); if (w0 != null) _warn(w0, "cant_fast_travel"); return false; }   // the game's own warning
            var gm = _world.GameManagerObject;
            var go = gm != null ? _gmCar(gm) as GameObject : null;
            bool own = false;
            foreach (var lc in _local) if (go != null && lc.Go == go && lc.Mine && lc.Active && !lc.IsProxy) own = true;
            if (TravelRules.FastTravelAllowed(_region, own)) return true;
            _phoneBlocked++;
            _log.Info("PHONE fast travel to car blocked: no car of this player in " + Regions.Name(_region) + " (no money charged)");
            var w = _psWarn(ps);
            if (w != null) _warn(w, "cant_fast_travel");
            return false;
        }

        public void HookError(string where, Exception ex) { _log.Error("TRAVEL hook " + where + " failed", ex); }

        // Exception inside the Select* handler: a scripted transition (menu closed) keeps the original; a menu click is
        // held (never an unauthorized travel that could carry a car) and the menu stays usable for a retry.
        public bool SelectFailSafe(object gm)
        {
            bool menu = false;
            try { menu = MenuOpen(gm); } catch { }
            if (!menu) return true;
            try { AbandonPending("hook failure"); } catch { }
            Quiet("Multiplayer: travel could not be coordinated, try again");
            return false;
        }

        // ======================================================================== reconcile local cars with the registry
        bool NeedsExitRetry() { foreach (var lc in _local) if (lc.ExitRequested) return true; return false; }

        void Reconcile(UnityEngine.Object gm, UnityEngine.Object ps, int region)
        {
            if (!_world.Synced) return;
            if (_ride != null && _ride.Stage == RideStage.Seated && !JointHolds(_ride.Id) && !RideStillValid(_ride, region)) ForceExitRide("the car is no longer presented as another player's car here", true);   // before any presentation change: the player is parented to the car
            var cur = ps != null ? _psVehicle(ps) as Component : null;
            foreach (var lc in _local)
            {
                if (lc.Go == null) continue;
                if (JointHolds(lc.Id)) continue;   // M4.3 Phase B: the held passenger sits in it until its own scene unloads
                VehicleRecord r;
                if (!Rec(lc.Id, out r)) continue;
                bool mine = r.Holder == _me && r.Region == region;
                if (mine != lc.Mine)
                {
                    _log.Info("VEHICLE " + CarName(lc.Id) + (mine ? " now simulated here (" : " not simulated here (") + r + ")");
                    lc.Policy.Reset(); lc.Park.Reset(); lc.Latch.Reset(); lc.Remote.Clear();   // fresh movement history per holder change
                }
                lc.Mine = mine;
                if (mine)
                {
                    if (lc.Pres != CarPresentation.Real) _log.Info("VEHICLE " + CarName(lc.Id) + " presentation " + lc.Pres + " -> Real (" + r + ")");
                    lc.Pres = CarPresentation.Real;
                    if (lc.IsProxy)
                    {
                        if (lc.Observed) ExitObserved(lc); else ExitProxy(lc);
                        // handoff in place (hideout copy): the frozen original resumes physics where it stands
                        if (gm != null) { _setGmCar(gm, lc.Go); var tr = _carTrunk(lc.Car); _setGmTrunk(gm, tr != null ? tr : lc.T); }
                    }
                    if (!lc.Active) Activate(gm, lc, region);
                    if (!lc.FuelLoaded)
                    {
                        lc.FuelLoaded = true;
                        ParkRecord p;
                        if (_view.TryGetPark(lc.Id, out p) && p.Fuel >= 0) { lc.Fuel = p.Fuel; if (lc == _fuelCar && ps != null) WriteGas(ps, lc.Fuel); }
                    }
                    continue;
                }
                if (lc == _fuelCar) { if (ps != null) lc.Fuel = ReadGas(ps); _fuelCar = null; }
                var pres = PresentationRules.Decide(r, region, _me);
                bool inside = lc.Active && ReferenceEquals(cur, lc.Car) && _sitting(lc.Car);
                if (inside)
                {
                    // the local player sits in a car it must not simulate: leave it first (original exit), then present it
                    if (!lc.ExitRequested) { lc.ExitRequested = true; _log.Warn("VEHICLE " + CarName(lc.Id) + " must be released (" + r + "): leaving it first"); _exitNow(lc.Car); }
                    continue;
                }
                lc.ExitRequested = false;
                if (PresentationRules.IsTravelProxy(pres))
                {
                    if (lc.Observed) ExitObserved(lc);
                    bool was = lc.IsProxy;
                    if (!lc.Active) ActivateProxyObject(gm, lc, region);
                    if (!lc.IsProxy) EnterProxy(lc, ps);
                    if (!was) KeepClearOfOtherCars(gm, lc);
                    if (lc.Pres != pres) _log.Info("VEHICLE " + CarName(lc.Id) + " hideout presentation " + lc.Pres + " -> " + pres + " (" + r + "): E opens the travel menu");
                    lc.Pres = pres;
                    continue;
                }
                if (PresentationRules.IsObserved(pres))
                {
                    if (lc.IsProxy && !lc.Observed) ExitProxy(lc);              // away proxy -> observed (the car came home)
                    if (!lc.Active) ActivateProxyObject(gm, lc, region);
                    if (!lc.Observed) EnterObserved(lc, region, ps);
                    if (lc.Pres != pres) _log.Info("VEHICLE " + CarName(lc.Id) + " presentation " + lc.Pres + " -> " + pres + " (" + r + "): original object observed, simulated by P" + r.Holder);
                    lc.Pres = pres;
                    continue;
                }
                if (lc.IsProxy) { if (lc.Observed) ExitObserved(lc); else ExitProxy(lc); }
                if (lc.Pres != pres) _log.Info("VEHICLE " + CarName(lc.Id) + " presentation " + lc.Pres + " -> " + pres + " (" + r + ")");
                lc.Pres = pres;
                if (!lc.Active) continue;
                lc.Go.SetActive(false);
                _deactivations++;
                _log.Info("VEHICLE " + CarName(lc.Id) + " local copy deactivated (" + r + ")");
            }
        }

        void Activate(UnityEngine.Object gm, LocalCar lc, int region)
        {
            ParkRecord p;
            bool pose = _view.TryGetPark(lc.Id, out p) && p.HasPose && p.Region == region;
            // (an observed car becomes Real in place in Reconcile; this path is for an inactive object)
            lc.Go.SetActive(true);
            if (pose)
            {
                lc.T.SetPositionAndRotation(new Vector3(p.Pos.X, p.Pos.Y, p.Pos.Z), new Quaternion(p.Rot.X, p.Rot.Y, p.Rot.Z, p.Rot.W));
                if (lc.Rb != null) { lc.Rb.velocity = Vector3.zero; lc.Rb.angularVelocity = Vector3.zero; }
            }
            try { _startVoid(lc.Car); } catch (Exception ex) { _log.Warn("VEHICLE StartVoid failed for " + CarName(lc.Id) + ": " + ex.Message); }
            if (gm != null) { _setGmCar(gm, lc.Go); var tr = _carTrunk(lc.Car); _setGmTrunk(gm, tr != null ? tr : lc.T); }
            _activations++;
            _log.Info("VEHICLE " + CarName(lc.Id) + " activated in " + Regions.Name(region) + (pose ? " at its parking pose " + p : " at its original position"));
        }

        // Interaction colliders (CarBox / van door / heavy-cargo trigger: no storage or cargo on a car simulated elsewhere) and
        // the body colliders (incl. the car-door colliders; not triggers, not wheels) with their car-local bounds.
        void CollectColliders(LocalCar lc)
        {
            lc.Blocked = new List<Collider>();
            foreach (var t in new[] { _carBoxT, _vanDoorT, _heavyT })
                if (t != null) foreach (var c in lc.Go.GetComponentsInChildren(t, true)) foreach (var col in c.GetComponents<Collider>()) if (col.enabled) lc.Blocked.Add(col);
            RecordCarBox(lc);
            lc.Solid = new List<Collider>();
            bool first = true; var b = new Bounds();
            foreach (var col in lc.Go.GetComponentsInChildren<Collider>(true))
            {
                if (col == null || col.isTrigger || col is WheelCollider || !col.enabled || lc.Blocked.Contains(col)) continue;
                lc.Solid.Add(col);
                // M4.3.2a: exact car-space box from the collider's own local box (box collider / mesh bounds) instead of its world
                // AABB, which grows with the car's yaw (up to ~1 m wider at 45 deg) and moved the exit/entry geometry outwards
                Bounds own; Transform ct = col.transform;
                var bc = col as BoxCollider; var mc = col as MeshCollider;
                if (bc != null) own = new Bounds(bc.center, bc.size);
                else if (mc != null && mc.sharedMesh != null) own = mc.sharedMesh.bounds;
                else { var wb = col.bounds; own = new Bounds(lc.T.InverseTransformPoint(wb.center), wb.size); ct = null; }
                for (int k = 0; k < 8; k++)
                {
                    var corner = new Vector3((k & 1) == 0 ? own.min.x : own.max.x, (k & 2) == 0 ? own.min.y : own.max.y, (k & 4) == 0 ? own.min.z : own.max.z);
                    var lp = ct != null ? lc.T.InverseTransformPoint(ct.TransformPoint(corner)) : corner;
                    if (first) { b = new Bounds(lp, Vector3.zero); first = false; } else b.Encapsulate(lp);
                }
            }
            lc.LocalBounds = b; lc.HasBounds = !first;
            // car's own ground level: lowest wheel contact (wheel collider centre - radius - half suspension travel, car space);
            // fallback box bottom - 0.55 m (0.6.1 logs: the body box bottom is ~0.58 m above the player's feet)
            float g = float.MaxValue;
            foreach (var wc in lc.Go.GetComponentsInChildren<WheelCollider>(true))
            {
                var c = lc.T.InverseTransformPoint(wc.transform.TransformPoint(wc.center));
                float rr = wc.radius * Mathf.Abs(wc.transform.lossyScale.y) / Mathf.Max(1e-4f, Mathf.Abs(lc.T.lossyScale.y));
                g = Mathf.Min(g, c.y - rr - wc.suspensionDistance * 0.5f);
            }
            lc.GroundY = g < float.MaxValue ? g : (lc.HasBounds ? b.min.y - 0.55f : 0f);
        }

        // M4.3.1: geometry of the original CarBox while its collider is still enabled (box in its own transform space when it is
        // a BoxCollider, else a car-local box).
        void RecordCarBox(LocalCar lc)
        {
            if (lc.HasCarBox || _carBoxT == null) return;
            var cb = lc.Go.GetComponentInChildren(_carBoxT, true);
            if (cb == null) return;
            var col = cb.GetComponent<Collider>();
            if (col == null || !col.enabled) return;
            lc.CarBox = cb; lc.CarBoxBox = col as BoxCollider;
            var wb = col.bounds;
            lc.CarBoxLocal = new Bounds(lc.T.InverseTransformPoint(wb.center), wb.size);
            lc.HasCarBox = true;
        }

        // A presentation appearing around the local player starts without body collision (never closes around the player);
        // CollisionFrame makes it solid as soon as the player is out.
        void GhostIfAroundPlayer(LocalCar lc, UnityEngine.Object ps)
        {
            lc.Ghost = false;
            var psc = ps as Component;
            if (psc == null || !lc.HasBounds || !Inside(lc.T, lc.LocalBounds, psc.transform.position)) return;
            foreach (var col in lc.Solid) if (col != null) col.enabled = false;
            lc.Ghost = true;
            _log.Info("VEHICLE " + CarName(lc.Id) + " appeared around the local player: body collision waits until the player steps out");
        }

        // Hideout travel proxy (AwayProxy) on the ORIGINAL car object: physics frozen (no movement, no impact costs),
        // storage/cargo colliders disabled, entering redirected to the original travel menu (TravelHooks). Restored when it
        // becomes Real or observed.
        void EnterProxy(LocalCar lc, UnityEngine.Object ps)
        {
            if (lc.Rb != null) { lc.SavedConstraints = lc.Rb.constraints; lc.Rb.velocity = Vector3.zero; lc.Rb.angularVelocity = Vector3.zero; lc.Rb.constraints = RigidbodyConstraints.FreezeAll; }
            CollectColliders(lc);
            foreach (var col in lc.Blocked) if (col != null) col.enabled = false;
            GhostIfAroundPlayer(lc, ps);
            lc.IsProxy = true;
        }

        void ExitProxy(LocalCar lc)
        {
            if (lc.Rb != null) lc.Rb.constraints = lc.SavedConstraints;
            RestoreColliders(lc);
            lc.Follow.Clear(); lc.FollowRecv = -100; lc.Remote.Clear();
            lc.IsProxy = false;
        }

        void RestoreColliders(LocalCar lc)
        {
            if (lc.Blocked != null) foreach (var col in lc.Blocked) if (col != null) col.enabled = true;
            if (lc.Solid != null) foreach (var col in lc.Solid) if (col != null) col.enabled = true;   // original state
            lc.Blocked = null; lc.Solid = null; lc.Ghost = false;
        }

        // M5.0 observed presentation (ParkedOther in the hideout, RemoteVisual elsewhere) on the ORIGINAL object: the body
        // becomes kinematic and follows the holder's snapshots (FixedStep); the original driving simulation (every enabled
        // EVP component: VehicleController, input, audio, tire effects) and the wheel colliders are paused, so the observer
        // never runs a second simulation, never applies wheel forces, never charges damage (CarController.OnCollisionEnter
        // returns unless the local player sits inside). Body colliders, car doors (Door + original animation), the original
        // enter trigger (CarController.Update shows it while a door is open) and the seat transforms stay as in the game.
        void EnterObserved(LocalCar lc, int region, UnityEngine.Object ps)
        {
            if (lc.Rb != null)
            {
                lc.SavedKinematic = lc.Rb.isKinematic; lc.SavedInterp = lc.Rb.interpolation; lc.SavedDetect = lc.Rb.collisionDetectionMode; lc.SavedConstraints = lc.Rb.constraints;
                if (!lc.Rb.isKinematic) { lc.Rb.velocity = Vector3.zero; lc.Rb.angularVelocity = Vector3.zero; }
                lc.Rb.isKinematic = true;
                lc.Rb.constraints = RigidbodyConstraints.None;
                lc.Rb.interpolation = RigidbodyInterpolation.Interpolate;
                lc.Rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                _observedBodies.Add(lc.Rb);
            }
            lc.Paused = new List<Behaviour>();
            var names = new List<string>();
            foreach (var bh in lc.Go.GetComponentsInChildren<Behaviour>(true))
            {
                if (bh == null || !bh.enabled) continue;
                var t = bh.GetType();
                if (t.Namespace != "EVP" && t.Name != "VehicleController") continue;
                bh.enabled = false; lc.Paused.Add(bh);
                if (!names.Contains(t.Name)) names.Add(t.Name);
            }
            lc.WheelCols = new List<Collider>();
            foreach (var wc in lc.Go.GetComponentsInChildren<WheelCollider>(true)) if (wc.enabled) { wc.enabled = false; lc.WheelCols.Add(wc); }
            // CarController.Update -> LoadTrunkItem (IL) instantiates THIS instance's global SPrefs "TrunkItem" into the car once
            // after enable: on a car simulated elsewhere that would create a duplicate physical item. Skipped while observed.
            if (_loadTrunk != null && _setLoadTrunk != null) { lc.SavedLoadTrunk = _loadTrunk(lc.Car); _setLoadTrunk(lc.Car, false); }
            CollectColliders(lc);
            foreach (var col in lc.Blocked) if (col != null) col.enabled = false;
            // M4.3.2b: the car is physically here, so its original CarBox stays usable (the shared, host-leased storage);
            // van door / heavy cargo stay blocked on a car simulated elsewhere. Travel proxies (AwayProxy) keep the CarBox off.
            if (CarBoxSync.Ready && _carBoxT != null)
                for (int k = lc.Blocked.Count - 1; k >= 0; k--)
                {
                    var col = lc.Blocked[k];
                    if (col != null && col.GetComponent(_carBoxT) != null) { col.enabled = true; lc.Blocked.RemoveAt(k); }
                }
            BindWheels(lc);
            lc.Observed = true; lc.IsProxy = true;
            ApplyParkPose(lc, region);
            GhostIfAroundPlayer(lc, ps);
            _observeEnter++;
            if (_warned.Add(90000 + lc.Kind))
                _log.Info("OBSERVED " + CarName(lc.Id) + ": kinematic=" + (lc.Rb != null) + " paused=" + string.Join(",", names.ToArray()) + " wheelColliders=" + lc.WheelCols.Count
                    + " bodyColliders=" + lc.Solid.Count + " storageColliders=" + lc.Blocked.Count + " wheelVisuals=" + (lc.Wheels != null ? lc.Wheels.Length : 0)
                    + " doors=" + (_carDoorL != null && _carDoorL(lc.Car) != null ? "L" : "-") + (_carDoorR != null && _carDoorR(lc.Car) != null ? "R" : "-"));
        }

        void ExitObserved(LocalCar lc)
        {
            if (lc.Rb != null)
            {
                _observedBodies.Remove(lc.Rb);
                lc.Rb.interpolation = lc.SavedInterp; lc.Rb.collisionDetectionMode = lc.SavedDetect; lc.Rb.constraints = lc.SavedConstraints;
                lc.Rb.isKinematic = lc.SavedKinematic;
                if (!lc.Rb.isKinematic) { lc.Rb.velocity = Vector3.zero; lc.Rb.angularVelocity = Vector3.zero; }
            }
            if (lc.WheelCols != null) foreach (var wc in lc.WheelCols) if (wc != null) wc.enabled = true;
            if (lc.Paused != null) foreach (var bh in lc.Paused) if (bh != null) bh.enabled = true;
            if (_setLoadTrunk != null && lc.Car != null) _setLoadTrunk(lc.Car, lc.SavedLoadTrunk);
            RestoreColliders(lc);
            lc.Paused = null; lc.WheelCols = null; lc.Wheels = null; lc.WheelHasPos = false; lc.Steer = lc.SteerTarget = 0f;
            lc.Follow.Clear(); lc.FollowRecv = -100; lc.Remote.Clear();
            lc.Observed = false; lc.IsProxy = false;
        }

        // Original wheel transforms of the car's EVP controller (wheels[i].wheelTransform / wheelCollider / steer).
        void BindWheels(LocalCar lc)
        {
            lc.Wheels = null; lc.WheelHasPos = false;
            try
            {
                var dt = _drivetrain != null ? _drivetrain(lc.Car) : null;
                var arr = dt != null && _evpWheels != null && _wT != null && _wCol != null ? _evpWheels.GetValue(dt) as Array : null;
                if (arr == null) return;
                var inv = Quaternion.Inverse(lc.T.rotation);
                var list = new List<WheelVis>();
                foreach (var w in arr)
                {
                    if (w == null) continue;
                    var t = _wT.GetValue(w) as Transform; var wc = _wCol.GetValue(w) as WheelCollider;
                    if (t == null || wc == null) continue;
                    var ct = wc.transform;
                    float radius = wc.radius * Mathf.Abs(ct.lossyScale.y);
                    if (radius < 0.05f) continue;
                    list.Add(new WheelVis { T = t, RestRel = inv * t.rotation, AxleL = inv * ct.right, UpL = inv * ct.up, Radius = radius, Steers = _wSteer != null && (bool)_wSteer.GetValue(w) });
                }
                if (list.Count > 0) lc.Wheels = list.ToArray();
            }
            catch (Exception ex) { if (_warned.Add(91000 + lc.Kind)) _log.Warn("VEHICLE wheel visuals unavailable for " + CarName(lc.Id) + ": " + ex.Message); }
        }

        // Proxy object that the original InitiateMainCar left inactive (e.g. not this save's current car, or the client's
        // sandbox has not bought the car the host owns): activated without touching GameManager.Car.
        void ActivateProxyObject(UnityEngine.Object gm, LocalCar lc, int region)
        {
            lc.Go.SetActive(true);
            try { _startVoid(lc.Car); } catch (Exception ex) { _log.Warn("VEHICLE StartVoid failed for " + CarName(lc.Id) + ": " + ex.Message); }
            _activations++;
        }

        void ApplyParkPose(LocalCar lc, int region)
        {
            if (lc.FollowRecv > 0) return;   // live snapshots drive it
            ParkRecord p;
            if (!_view.TryGetPark(lc.Id, out p) || !p.HasPose || p.Region != region) return;
            var pos = new Vector3(p.Pos.X, p.Pos.Y, p.Pos.Z); var rot = new Quaternion(p.Rot.X, p.Rot.Y, p.Rot.Z, p.Rot.W);
            lc.T.SetPositionAndRotation(pos, rot);
            if (lc.Rb != null && lc.Rb.isKinematic) { lc.Rb.position = pos; lc.Rb.rotation = rot; }
        }

        // An away proxy keeps its last hideout spot unless that overlaps another car; then the free original hideout spot
        // (GameManager.SpawnHideoutCar / VehiclesManager.secondCarHideoutPos) farthest from the other cars is used.
        void KeepClearOfOtherCars(UnityEngine.Object gm, LocalCar lc)
        {
            if (!Overlaps(lc, lc.T.position)) return;
            var vm = gm != null ? _gmVM(gm) : null;
            var spots = new List<Transform>();
            var a = gm != null && _gmHideoutCar != null ? _gmHideoutCar(gm) as Transform : null; if (a != null) spots.Add(a);
            var b = vm != null && _vmSecondPos != null ? _vmSecondPos(vm) as Transform : null; if (b != null) spots.Add(b);
            foreach (var sp in spots)
                if (!Overlaps(lc, sp.position)) { lc.T.SetPositionAndRotation(sp.position, sp.rotation); _log.Info("VEHICLE " + CarName(lc.Id) + " travel proxy moved to the free hideout spot " + sp.name); return; }
        }

        bool Overlaps(LocalCar lc, Vector3 p)
        {
            foreach (var o in _local) if (o != lc && o.Active && Flat(o.T.position, p) < 3.5f) return true;
            return false;
        }

        // TravelHooks: prefix of CarController.EnterVehicle (reached only through the original E interaction:
        // PlayerScript.Update raycast tag "SitCar" + "interact" -> EnterCar.EnterVehicle). Decides per presentation:
        // real car -> original; away proxy -> travel menu; car simulated by the other instance -> handoff request
        // (asynchronous; the original entry is resumed exactly once after the host grants it).
        public bool AllowEnterVehicle(object car)
        {
            LocalCar lc = null;
            foreach (var l in _local) if (ReferenceEquals(l.Car, car)) lc = l;
            if (lc == null) return true;                                   // stolen / NPC / mission cars: original behaviour
            if (_enterResume != null && ReferenceEquals(_enterResume, lc)) { _enterResume = null; return true; }   // our continuation
            if (!lc.IsProxy) return true;                                  // simulated here (lease claimed by StreamFrame) or not presented yet: original
            var pres = lc.Pres;
            VehicleRecord r;
            if (!Rec(lc.Id, out r)) return false;
            bool settled = lc.Remote.Settled(Now);
            var act = PresentationRules.Enter(pres, r, _me, settled, lc.Remote.Stopped(Now));
            switch (act)
            {
                case EnterAction.Original: return true;
                case EnterAction.TravelMenu: OpenTravelMenu(lc); return false;
                case EnterAction.Occupied: Quiet("Multiplayer: both seats are taken"); return false;
                case EnterAction.RequestPassenger: RequestRide(lc, r, true); return false;
                case EnterAction.Wait: Quiet("Multiplayer: wait until the car has stopped"); return false;
                case EnterAction.Unavailable: Quiet("Multiplayer: this car is not available right now"); return false;
            }
            RequestTake(lc, r);
            return false;
        }

        void RequestTake(LocalCar lc, VehicleRecord r)
        {
            if (_take != null) return;                                     // one request at a time (repeated E ignored)
            if (_enterVehicle == null) { Quiet("Multiplayer: driver handoff unavailable"); return; }
            _takeRequests++;
            if (_host)
            {
                string why;
                var code = _world.Host.TakeOver(_me, lc.Id, _world.Clock, out why);
                _world.FlushOutbox();
                _log.Info("VEHICLE " + CarName(lc.Id) + " driver handoff requested by the host -> " + code + (why != null ? " (" + why + ")" : ""));
                _take = new Take { Id = lc.Id, Car = lc, At = Now };
                TakeResult(code);
                return;
            }
            if (!_node.IsConnected || !_world.Synced) { Quiet("Multiplayer: not connected to the host"); return; }
            var e = new StateEntry { Scope = StateScope.Session, ScopeArg = lc.Id, Key = WorldKeys.VehicleTake, Value = new byte[] { 1 } };
            uint id = _world.SendProposal(e);
            _take = new Take { Id = lc.Id, Car = lc, ProposalId = id, At = Now };
            _log.Info("VEHICLE " + CarName(lc.Id) + " driver handoff requested #" + id + " (" + r + ")");
        }

        void TakeResult(ProposalCode code)
        {
            if (_take == null) return;
            if (code == ProposalCode.Accepted || code == ProposalCode.Unchanged)
            {
                _takeGranted++;
                _take.Granted = true; _take.At = Now;
                _reconcile = true;                                         // the registry delta precedes the result
                _log.Info("VEHICLE " + CarName(_take.Id) + " driver handoff granted (" + code + "): entering once the car is simulated here");
                return;
            }
            _takeDenied++;
            Quiet(code == ProposalCode.Busy ? "Multiplayer: the car is in use or still moving" : "Multiplayer: the host refused the car (" + code + ")");
            EndTake("refused: " + code, false);
        }

        void EndTake(string why, bool notify)
        {
            if (_take == null) return;
            _log.Info("VEHICLE " + CarName(_take.Id) + " driver handoff ended (" + why + ")");
            if (notify) Quiet("Multiplayer: " + why + ", try again");
            _take = null;
        }

        // After the grant: once Reconcile made the car Real here, run the original entry exactly once if the player is
        // still next to it and on foot. Otherwise the player simply keeps simulating the parked car (no seat taken).
        void EnterFrame(double now, UnityEngine.Object ps)
        {
            var t = _take; var lc = t.Car;
            if (now - t.At > 3.0) { EndTake("entry not possible after the grant (car simulated here, seat stays free)", false); return; }
            if (ps == null || lc.Car == null || !lc.Mine || !lc.Active || lc.IsProxy) return;   // wait for the registry delta / reconcile
            var psc = ps as Component;
            if (_psInVehicle(ps) || _psChase(ps) || psc == null) { EndTake("player busy", false); return; }
            if (Vector3.Distance(psc.transform.position, lc.T.position) > 6f) { EndTake("player walked away", false); return; }
            _take = null;
            _enterResume = lc;
            try { _enterVehicle(lc.Car); _takeEntered++; _log.Info("VEHICLE " + CarName(lc.Id) + " entered after the handoff (original CarController.EnterVehicle)"); }
            catch (Exception ex) { _log.Error("VEHICLE entry after handoff failed for " + CarName(lc.Id), ex); }
            finally { if (_enterResume != null) { _enterResume = null; _log.Warn("VEHICLE entry continuation not consumed"); } }
        }

        // ---- local collision of presentations (never authoritative) ----
        // M5.0: presented cars are solid in every state (PresentationRules.Collision); the only exception is a car that
        // appeared around the local player, which turns solid once the player has stepped out of its bounds.
        void CollisionFrame(double now, UnityEngine.Object gm)
        {
            Vector3 player = Vector3.zero; bool havePlayer = false;
            var ps = gm != null ? _gmPS(gm) as Component : null;
            if (ps != null) { player = ps.transform.position; havePlayer = true; }
            for (int i = 0; i < _local.Count; i++)
            {
                var lc = _local[i];
                if (!lc.IsProxy || lc.Solid == null || !lc.Active) continue;
                VehicleRecord r;
                if (!Rec(lc.Id, out r)) continue;
                bool ghost = PresentationRules.Collision(lc.Pres, r, true) != CollisionMode.Solid;
                if (ghost == lc.Ghost) continue;
                if (!ghost && havePlayer && lc.HasBounds && Inside(lc.T, lc.LocalBounds, player)) continue;   // never close around the player
                lc.Ghost = ghost; _solidSwitches++;
                for (int k = 0; k < lc.Solid.Count; k++) if (lc.Solid[k] != null) lc.Solid[k].enabled = !ghost;
            }
        }

        // M5.0: a CharacterController is not pushed by kinematic bodies, so a moving observed car could close around a
        // standing player. Bounded guard, only while the player is inside an observed car's bounds (+1 m): separate the
        // player's original capsule (CapsuleCollider next to the CharacterController, else the controller) from that car's
        // colliders with Physics.ComputePenetration and move it out horizontally with CharacterController.Move (respects
        // walls). Logged as pushOuts in VEHICLE_STATS.
        void PushOutFrame(UnityEngine.Object gm)
        {
            if (_observedBodies.Count == 0 || gm == null) return;
            var ps = _gmPS(gm) as Component;
            if (ps == null || _psInVehicle(ps)) return;
            var cc = _psCC(ps) as CharacterController;
            if (cc == null || !cc.enabled) return;
            var pt = ps.transform; var feet = pt.position;
            LocalCar near = null;
            for (int i = 0; i < _local.Count; i++)
            {
                var lc = _local[i];
                if (lc.Observed && lc.Active && !lc.Ghost && lc.HasBounds && lc.Rb != null && Inside(lc.T, lc.LocalBounds, feet)) { near = lc; break; }
            }
            if (near == null) return;
            if (!ReferenceEquals(_capsuleOwner, ps)) { _capsuleOwner = ps; _capsule = ps.GetComponent<CapsuleCollider>(); }
            Collider self = _capsule != null && _capsule.enabled ? (Collider)_capsule : cc;
            var center = pt.TransformPoint(cc.center);
            float scale = Mathf.Max(Mathf.Abs(pt.lossyScale.x), Mathf.Abs(pt.lossyScale.z));
            float radius = cc.radius * scale, half = Mathf.Max(cc.height * Mathf.Abs(pt.lossyScale.y) * 0.5f - radius, 0f);
            var up = pt.up;
            int n = Physics.OverlapCapsuleNonAlloc(center + up * half, center - up * half, radius, _near, ~0, QueryTriggerInteraction.Ignore);
            var push = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                var col = _near[i];
                if (col == null || col.attachedRigidbody != near.Rb) continue;
                Vector3 dir; float dist;
                if (Physics.ComputePenetration(self, self.transform.position, self.transform.rotation, col, col.transform.position, col.transform.rotation, out dir, out dist)) push += dir * dist;
            }
            push.y = 0f;
            if (push.sqrMagnitude < 1e-6f) return;
            if (push.sqrMagnitude > 4f) push = push.normalized * 2f;   // bounded per frame
            cc.Move(push + push.normalized * 0.01f);
            _pushOuts++;
        }

        // M5.0 observer wheel visuals on the original wheel transforms: spin from the car's own rendered motion (distance along
        // its forward axis / rolling radius), steering from the holder's EVP steer angle (snapshot). No wheel physics.
        void WheelFrame()
        {
            if (_observedBodies.Count == 0) return;
            float dt = Time.deltaTime;
            for (int i = 0; i < _local.Count; i++)
            {
                var lc = _local[i];
                if (!lc.Observed || lc.Wheels == null || !lc.Active) continue;
                var pos = lc.T.position; var rot = lc.T.rotation;
                float d = lc.WheelHasPos ? Vector3.Dot(pos - lc.WheelLastPos, rot * Vector3.forward) : 0f;
                if (Mathf.Abs(d) > 5f) d = 0f;   // teleport, not rolling
                lc.WheelLastPos = pos; lc.WheelHasPos = true;
                lc.Steer = Mathf.MoveTowards(lc.Steer, lc.SteerTarget, 180f * dt);
                for (int k = 0; k < lc.Wheels.Length; k++)
                {
                    var w = lc.Wheels[k];
                    if (w.T == null) continue;
                    w.Spin = (w.Spin + d / w.Radius * Mathf.Rad2Deg) % 360f;
                    w.T.rotation = rot * (Quaternion.AngleAxis(w.Steers ? lc.Steer : 0f, w.UpL) * Quaternion.AngleAxis(w.Spin, w.AxleL) * w.RestRel);
                }
            }
        }

        // ---- M5.0 car doors: original Door components, host-committed state ("vdoor") ----
        // Every car presented here (simulated or observed): local door changes (the local player's E on the door, or the
        // local original CarController code such as the entry close / exit open coroutines) are reported; the committed state
        // is applied to the original Door objects (Door.Update animates them).
        void DoorFrame(int region)
        {
            if (!DoorApi || !_world.Synced || region == Regions.None || !TravelRules.IsFleetRegion(region)) return;
            for (int i = 0; i < _local.Count; i++)
            {
                var lc = _local[i];
                if (!lc.Active || !(lc.Observed || (lc.Mine && !lc.IsProxy))) continue;
                VehicleRecord r;
                if (!Rec(lc.Id, out r) || r.Region != region) continue;
                if (!lc.DoorsBound)
                {
                    lc.DoorsBound = true;
                    var dl = _carDoorL(lc.Car) as Component; var dr = _carDoorR(lc.Car) as Component;
                    lc.Doors = dl != null || dr != null ? new[] { dl, dr } : null;
                    if (lc.Doors != null) lc.DoorRep.Bind(ReadDoors(lc));
                    lc.SlowAt[0] = lc.SlowAt[1] = SlowNone; lc.SlowRep[0] = lc.SlowRep[1] = false;
                }
                if (lc.Doors == null) continue;
                byte actual = ReadDoors(lc);
                byte changed = lc.DoorRep.TakeLocalChanges(actual);
                for (int d = 0; d < VehicleDoors.Count; d++)
                {
                    var o = lc.Doors[d];
                    if (VehicleDoors.IsOpen(changed, d))
                    {
                        // Door.Update (IL 0x0047..0x00c9): a held E toggles isOpen when the slow movement starts (openingSlowly)
                        bool slow = SlowDoorApi && o != null && _doorSlowly(o);
                        lc.SlowRep[d] = slow;
                        ReportDoor(lc, d, VehicleDoors.IsOpen(actual, d), slow ? DoorMode.Slow : DoorMode.Quick);
                    }
                    else if (lc.SlowRep[d] && o != null && !_doorSlowly(o))
                    {
                        // Door.SlowFailed (IL 0x0064..0x007f): E released early -> same movement finished at 150 x speedMultiplier
                        // (loud clip); a completed slow swing ends with Door.Resett at 17/70 x speedMultiplier instead.
                        lc.SlowRep[d] = false;
                        if (_doorOpening(o) && _doorSpeedNow(o) >= 100f * _doorSpeedMul(o)) ReportDoor(lc, d, VehicleDoors.IsOpen(actual, d), DoorMode.FastFinish);
                    }
                }
                byte want;
                if (lc.DoorRep.Wanted(out want) && !DoorBusy(lc)) ApplyDoors(lc);
                SlowDoorFrame(lc);
            }
        }

        byte ReadDoors(LocalCar lc)
        {
            byte m = 0;
            for (int d = 0; d < VehicleDoors.Count; d++) if (lc.Doors[d] != null && _doorIsOpen(lc.Doors[d])) m = VehicleDoors.With(m, d, true);
            return m;
        }

        // the local player is in the middle of the original slow door interaction: let it finish first
        bool DoorBusy(LocalCar lc)
        {
            for (int d = 0; d < VehicleDoors.Count; d++) { var o = lc.Doors[d]; if (o != null && (_doorLoadSlow(o) || _doorSlowly(o))) return true; }
            return false;
        }

        void ReportDoor(LocalCar lc, int door, bool open, DoorMode mode)
        {
            _doorReports++;
            if (mode != DoorMode.Quick) _slowReports++;
            if (_host)
            {
                string why;
                var code = _world.Host.SetDoor(_me, lc.Id, door, open, mode, _world.Clock, out why);
                _world.FlushOutbox();
                lc.DoorRep.OnCommitted(_world.Host.DoorMask(lc.Id));   // refused -> the next Wanted() restores the committed state
                if (code == ProposalCode.Accepted) _commits++;
                else if (code != ProposalCode.Unchanged)
                {
                    _doorRefused++;
                    _log.Info("DOOR " + CarName(lc.Id) + " " + (door == 0 ? "L" : "R") + (open ? " open" : " close") + " " + mode + " refused: " + code + (why != null ? " (" + why + ")" : ""));
                    if (code == ProposalCode.Busy) Quiet("Multiplayer: the car is moving");
                }
                return;
            }
            if (!_node.IsConnected) return;
            var e = new StateEntry { Scope = StateScope.Session, ScopeArg = lc.Id, Key = WorldKeys.VehicleDoor, Value = VehicleDoors.Proposal(door, open, mode) };
            uint id = _world.SendProposal(e);
            _doorIds[id] = lc.Id;
            lc.DoorRep.Sent();
        }

        // Applies the committed door state to the original Door objects (DoorReplica.Plan per door): quick swing (Door.Use
        // without its local-player side effects), slow swing (the hold-E movement), or finishing a slow swing fast (SlowFailed).
        void ApplyDoors(LocalCar lc)
        {
            for (int d = 0; d < VehicleDoors.Count; d++)
            {
                var door = lc.Doors[d];
                if (door == null) continue;
                switch (lc.DoorRep.Plan(d))
                {
                    case DoorStep.OpenQuick: SwingDoor(door, true); lc.SlowAt[d] = SlowNone; break;
                    case DoorStep.CloseQuick: SwingDoor(door, false); lc.SlowAt[d] = SlowNone; break;
                    case DoorStep.OpenSlow: if (SwingSlow(door, true)) { lc.SlowAt[d] = Now; _slowApplies++; } else lc.SlowAt[d] = SlowNone; break;
                    case DoorStep.CloseSlow: if (SwingSlow(door, false)) { lc.SlowAt[d] = Now; _slowApplies++; } else lc.SlowAt[d] = SlowNone; break;
                    case DoorStep.FinishFast: FinishFast(door); lc.SlowAt[d] = SlowNone; _fastFinishes++; break;
                }
            }
            lc.DoorRep.Applied();
            for (int d = 0; d < VehicleDoors.Count; d++) if (lc.SlowAt[d] == SlowNone) lc.DoorRep.SlowEnded(d);
            _doorApplies++;
        }

        // Door.Use() (IL) minus its local-player side effects (PlayerMotorBehavior.canMove, mouse sensitivity, noise): isOpen,
        // openedByPlayer = isOpen, opening = true, openSpeed = 150 x speedMultiplier, canUse = false and the open/close clip
        // on the door's AudioSource; the original Door.Update then swings the door and Door.Resett() re-enables it.
        void SwingDoor(Component door, bool open)
        {
            _doorSetOpen(door, open); _doorSetByPlayer(door, open); _doorSetOpening(door, true);
            _doorSetSpeed(door, 150f * _doorSpeedMul(door)); _doorSetCanUse(door, false);
            var src = _doorSound != null ? _doorSound(door) as AudioSource : null;
            var clip = (open ? (_doorClipOpen != null ? _doorClipOpen(door) : null) : (_doorClipClose != null ? _doorClipClose(door) : null)) as AudioClip;
            if (src != null && clip != null) { if (_doorVolume != null) src.volume = _doorVolume(door); src.pitch = 1f; src.PlayOneShot(clip); }
        }

        // The original slow (hold-E) movement replayed on a door: Door.Update's load_slow start (isOpen toggled, opening,
        // openSpeed2 = 0) and its first slow frame (DoorSqueak on DoorSound at volume 0.3, pitch 0.9-1.1). openingSlowly itself
        // is NOT set: Door.Update's slow branch writes the shared HUD fill (DoorOpenButton) that PlayerScript.Update reads to end
        // the LOCAL player's slow open (IL_453d), and restores the local player's canMove/mouse sensitivity at 0.8 - wrong for
        // an observer (e.g. a seated passenger). SlowDoorFrame drives openSpeed through the same timeline instead.
        bool SwingSlow(Component door, bool open)
        {
            if (!SlowDoorApi) { SwingDoor(door, open); return false; }
            _doorSetOpen(door, open); _doorSetByPlayer(door, open); _doorSetOpening(door, true); _doorSetCanUse(door, false);
            _doorSetSpeed(door, 0f); _doorSetSpeed2(door, 0f);
            var src = _doorSound != null ? _doorSound(door) as AudioSource : null;
            var squeak = _doorSqueak != null ? _doorSqueak(door) as AudioClip : null;
            if (src != null) { src.Stop(); if (squeak != null) { src.volume = 0.3f; src.pitch = UnityEngine.Random.Range(0.9f, 1.1f); src.clip = squeak; src.Play(); } }
            return true;
        }

        // Door.SlowFailed (IL) minus its local-player side effects: openSpeed = 150 x speedMultiplier, volume = maxVolume,
        // pitch 1, Stop, the open/close clip; the movement continues to the same state.
        void FinishFast(Component door)
        {
            _doorSetSpeed(door, 150f * _doorSpeedMul(door));
            var src = _doorSound != null ? _doorSound(door) as AudioSource : null;
            bool open = _doorIsOpen(door);
            var clip = (open ? (_doorClipOpen != null ? _doorClipOpen(door) : null) : (_doorClipClose != null ? _doorClipClose(door) : null)) as AudioClip;
            if (src != null) { if (_doorVolume != null) src.volume = _doorVolume(door); src.pitch = 1f; src.Stop(); if (clip != null) src.PlayOneShot(clip); }
        }

        // Slow-swing timeline (Door.Update IL 0x0100..0x02ba with openingSlowly): speed ramps by 45 x slowOpenSpeedMulti deg/s^2
        // up to 17 x speedMultiplier; slowMoving grows by 0.85 x slowOpenSpeedMulti per second and above 0.8 the door finishes at
        // 70 x speedMultiplier. Door.Update's own acceleration follows openSpeed within a frame. Only doors being replayed.
        // A fast-finish commit that arrives after the 70 phase began is still honoured (the remote player released E before
        // 0.8 in their own timeline); the slow mark is kept until the door reached its state.
        const double SlowNone = -1, SlowFinishing = -2;
        void SlowDoorFrame(LocalCar lc)
        {
            for (int d = 0; d < VehicleDoors.Count; d++)
            {
                if (lc.SlowAt[d] == SlowNone) continue;
                var o = lc.Doors[d];
                if (o == null || !_doorOpening(o)) { lc.SlowAt[d] = SlowNone; lc.DoorRep.SlowEnded(d); continue; }   // reached its state (Door.Resett)
                if (lc.SlowAt[d] == SlowFinishing) continue;
                float mulS = Mathf.Max(0.01f, _doorSlowMul(o)), mul = _doorSpeedMul(o);
                float e = (float)(Now - lc.SlowAt[d]);
                if (e * 0.85f * mulS > 0.8f) { _doorSetSpeed(o, 70f * mul); lc.SlowAt[d] = SlowFinishing; continue; }
                _doorSetSpeed(o, Mathf.Min(17f * mul, e * 45f * mulS));
            }
        }

        // ======================================================================== M4.3 passenger seat (local player)
        // The original game has no second human seat; its companion (CodenameAI.Update, IL) simply occupies
        // CarController.associateSitPos while the player drives. A human passenger therefore uses a minimal adapter built
        // from the original pieces: the original doors (E opens, the SitCar prompt appears, E requests the seat), the
        // non-driver part of CarController.EnterVehicle (weapons hidden, crouch off, PlayerMotorBehavior.canMove off,
        // CharacterController + CapsuleCollider off, player parented to a seat like RunCar parents the driver to
        // sit_transform, CarController.Update's dt x 3 seat lerp, door closed after 0.25 s like L_/R_Door_Close_Delay), the
        // camera part of CarController.Set_TPP for the driver's camera MODE, and the non-driver part of ExitCar (door
        // opens, unparent, CarExitRot, camera clip GetOutCar, controls back after 0.6 s). Never set: current_vehicle,
        // is_in_vehicle, is_sitting_inside, controls, engine, lease or simulation authority.
        enum RideStage { Requested, Granted, Seated, Exiting }
        sealed class Ride { public ushort Id; public LocalCar Car; public uint ProposalId, ConfirmId; public double At; public RideStage Stage; public Transform Seat; public bool Tpp, DoorsClosed, ViaDoor, KeptInteract; }
        Ride _ride;
        public const double RideGrantSeconds = 4.0, RideExitSeconds = 0.6;
        long _rideRequests, _rideGranted, _rideDenied, _rideSeated, _rideExits, _rideForced, _camSwitches;

        // M4.3 Phase B: a seated passenger never chooses a destination (the original companion seat has no travel action); a
        // driver with a passenger aboard travels jointly (the host decides, WorldHost.TravelCheck).
        string SeatTravelBlock()
        {
            if (_ride != null || _jStage != JStage.None) return "passenger: only the driver chooses the destination";
            return null;
        }

        // ======================================================================== M4.3 Phase B joint travel (passenger side)
        // The host records one JointTrip (registry "jointTrip"). This instance, when it is the trip's passenger:
        //   Hold    - seated in the origin until the host saw the driver leave: the car keeps its presentation (its record
        //             already names the destination), the seat is kept, exit/storage/menu are frozen;
        //   Depart  - starts its OWN original GameManager.Select<Dest> once (save + loading screen + MainScene reload);
        //   Wait    - at the destination the original spawns an on-foot player (IsStartingCar is skipped: Join context);
        //             it is kept frozen (no movement, no interaction) and reported as seated until the car is live;
        //   Seat    - the established passenger seat path (EnterSeat: seat anchor, first-person seat camera, camera mode from
        //             the current record, confirm to the host) - no second seat request, the host kept the seat;
        //   Recover - forward recovery: complete on-foot player, checked placement near the car, seat released.
        // The driver's side is the unchanged M4.2 Carry travel (IsStartingCar seats it and it claims its normal lease).
        enum JStage { None, Holding, Departing, Waiting }
        JStage _jStage = JStage.None;
        JointTrip _trip; bool _hasTrip;
        uint _jId; ushort _jCar; double _jAt, _jArriveAt; UnityEngine.Object _jFrozenPs;
        uint _jLastDeparted;    // trip id already departed for (a stale or repeated entry never starts a second travel)
        long _jHolds, _jDepartures, _jDepartFailed, _jSeated, _jRecovered, _jOnFoot, _jTownBlocked;
        double _jLoadMax, _jReadyMax;

        bool JointHolds(ushort id) { return _jStage == JStage.Holding && _jCar == id || _jStage == JStage.Departing && _jCar == id; }
        bool JointWaiting { get { return _jStage == JStage.Waiting; } }

        void ReadTrip(StateRegistry reg)
        {
            var e = reg.Get(StateScope.Session, 0, WorldKeys.JointTrip);
            JointTrip t;
            _hasTrip = e != null && JointTrip.TryParse(e.Value, out t) && (_trip = t).Id != 0;
        }

        void JointFrame(double now, UnityEngine.Object gm, int region)
        {
            if (_jStage == JStage.None && (!_hasTrip || _trip.Passenger != _me || !_trip.Active)) return;   // nothing to do (common case)
            var ps = gm != null ? _gmPS(gm) : null;
            bool seated = _ride != null && _ride.Stage == RideStage.Seated && _hasTrip && _ride.Id == _trip.Car;
            bool departed = _jStage == JStage.Departing || _jStage == JStage.Waiting;
            bool synced = _node.IsConnected && _world.Synced;
            bool timedOut = false, ready = false;
            LocalCar lc = null;
            if (departed)
            {
                if (_jStage == JStage.Departing) timedOut = now - _jAt > JointRules.DepartStuckSeconds;
                else timedOut = _jArriveAt >= 0 && now - _jArriveAt > JointRules.PassengerWaitSeconds;
                if (!synced) timedOut = true;                        // the host already ended the trip for a lost connection
                if (_jStage == JStage.Waiting && region == _trip.Dest && gm != null && _gmLoaded(gm))
                {
                    foreach (var l in _local) if (l.Id == _jCar) lc = l;
                    VehicleRecord r;
                    if (lc != null && Rec(_jCar, out r))
                    {
                        bool presented = lc.Car != null && lc.Observed && lc.Active;
                        bool anchor = lc.HasPassengerRoot && lc.PassengerSeat.Valid;
                        bool fresh = lc.Follow.Count > 0 && lc.FollowRecv > _jArriveAt;
                        ready = JointRules.ReadyToSeat(r, _trip, _me, region, presented, anchor, fresh) && ps != null;
                    }
                }
            }
            var step = JointRules.Passenger(_hasTrip && _trip.Id == (departed ? _jId : _trip.Id), _trip, _me, region, seated, departed, ready, timedOut);
            switch (step)
            {
                case JointStep.None:
                    if (_jStage == JStage.Holding) { _log.Info("JOINT trip#" + _jId + " hold ended (" + (_hasTrip ? _trip.Phase.ToString() : "no trip") + "): normal seat rules"); _jStage = JStage.None; _jCar = 0; _reconcile = true; }
                    return;
                case JointStep.Hold:
                    if (_jStage != JStage.Holding)
                    {
                        _jStage = JStage.Holding; _jId = _trip.Id; _jCar = _trip.Car; _jAt = now; _jHolds++;
                        _log.Info("JOINT " + _trip + ": seated passenger holds in " + Regions.Name(region) + " until the driver leaves (exit, storage and menu frozen)");
                    }
                    return;
                case JointStep.Depart:
                    if (_trip.Id == _jLastDeparted) return;
                    JointDepart(now, gm, region);
                    return;
                case JointStep.Wait:
                    if (_jStage == JStage.Waiting && ps != null && region == _trip.Dest) JointFreeze(ps);
                    return;
                case JointStep.Seat:
                    JointSeat(now, ps, lc);
                    return;
                case JointStep.Recover:
                {
                    bool onFoot = _hasTrip && _trip.Id == _jId && region == _trip.Dest && !TravelRules.SeatsOnArrival(_trip.Dest) && _trip.Phase != JointPhase.Failed && _trip.Phase != JointPhase.Cancelled && !timedOut;
                    JointRecover(now, ps, region, onFoot ? "original arrival on foot at " + Regions.Name(region) : timedOut ? (synced ? "timed out" : "disconnected") : _hasTrip ? "trip " + _trip.Phase : "no trip", synced && timedOut, onFoot);
                    return;
                }
            }
        }

        void JointDepart(double now, UnityEngine.Object gm, int region)
        {
            MethodInfo m;
            var t = _trip;
            _jLastDeparted = t.Id; _jId = t.Id; _jCar = t.Car;
            if (gm == null || !TravelHooks.SelectByDest.TryGetValue(t.Dest, out m))
            {
                _jDepartFailed++;
                _log.Error("JOINT " + t + ": no original travel method for " + Regions.Name(t.Dest) + ": leaving the seat (driver travels alone)");
                _jStage = JStage.None; _jCar = 0;
                ForceExitRide("joint departure impossible", true);
                return;
            }
            _jStage = JStage.Departing; _jAt = now; _jArriveAt = -1; _jDepartures++;
            _ctx = new Arrival { Dest = t.Dest, Outcome = TravelOutcome.Join, Car = t.Car, At = now };   // InitiateMainCar: no car of ours; IsStartingCar: skipped
            _travelStatus = "joint journey to " + Regions.Name(t.Dest);
            _log.Info("JOINT " + t + ": driver left - starting the original " + m.Name + " on this instance (passenger, no second travel command)");
            _resume.Arm(m);
            try { m.Invoke(gm, null); }
            catch (Exception ex)
            {
                _jDepartFailed++;
                _log.Error("JOINT original " + m.Name + " failed: leaving the seat (driver travels alone)", ex);
                _ctx = null; _jStage = JStage.None; _jCar = 0;
                ForceExitRide("joint departure failed", true);
            }
            finally { if (_resume.Armed) { _resume.Disarm(); _log.Error("JOINT continuation guard was not consumed (original not re-entered)"); } }
        }

        // The on-foot player the original creates at the destination stays where it spawned: no movement, no interaction
        // (re-applied every waiting frame: the original's own post-load code may re-enable them), shown to the other player
        // as seated in the car (the host still records this player as its passenger).
        void JointFreeze(UnityEngine.Object ps)
        {
            var pmb = _psPMB(ps);
            if (pmb != null) _pmbSetCanMove(pmb, false);
            _setCanInteract(ps, false);
            if (_players != null) _players.LocalSeated = true;
            if (!ReferenceEquals(_jFrozenPs, ps)) { _jFrozenPs = ps; _log.Info("JOINT trip#" + _jId + " arrived in " + Regions.Name(_trip.Dest) + ": on-foot player frozen until the car is live here"); }
        }

        void JointSeat(double now, UnityEngine.Object ps, LocalCar lc)
        {
            double readyIn = _jArriveAt >= 0 ? now - _jArriveAt : -1;
            _jStage = JStage.None; _jFrozenPs = null;
            _placed = true; _ctx = null;                           // no on-foot placement: the player goes straight into the seat
            var t = new Ride { Id = lc.Id, Car = lc, At = now, Stage = RideStage.Granted, ViaDoor = false };
            _ride = t;
            EnterSeat(t, ps);
            if (_ride == t && t.Stage == RideStage.Seated)
            {
                _jSeated++;
                if (readyIn > _jReadyMax) _jReadyMax = readyIn;
                _log.Info("JOINT trip#" + _jId + " passenger re-seated in " + CarName(lc.Id) + " " + readyIn.ToString("0.0") + " s after arrival (car simulated by P" + _trip.Driver + ", camera mode follows the driver)");
            }
        }

        void JointRecover(double now, UnityEngine.Object ps, int region, string why, bool releaseSeat, bool onFoot)
        {
            var stage = _jStage;
            ushort car = _jCar;
            _jStage = JStage.None; _jCar = 0;
            if (onFoot) _jOnFoot++; else _jRecovered++;
            if (stage == JStage.Departing && _ride != null) { ForceExitRide("joint journey ended before the scene changed (" + why + ")", true); _jFrozenPs = null; return; }
            if (releaseSeat && car != VehicleIds.None) { VehicleRecord r; if (Rec(car, out r) && r.Passenger == _me) ReleaseSeat(car); }
            if (ps != null && ReferenceEquals(_jFrozenPs, ps)) RestoreOnFoot(ps);
            _jFrozenPs = null;
            if (_players != null && _ride == null) _players.LocalSeated = false;
            if (_ctx != null && _ctx.Dest == region) _placed = false;   // checked on-foot placement near the car (TryPlace, Join context)
            if (onFoot) _log.Info("JOINT trip#" + _jId + " passenger arrived with the car (" + why + ", like the driver): placed on foot near it");
            else _log.Warn("JOINT trip#" + _jId + " forward recovery in " + Regions.Name(region) + " (" + why + "): on-foot controls restored" + (releaseSeat ? ", seat released" : ""));
        }

        // TravelHooks: a seated passenger's own "Trigger Map" child entering a MapBorder (or any other opener) must not open the
        // menu on the passenger's instance - in the original game the companion in that seat never travels by itself.
        public bool AllowTownSelection(object ps)
        {
            if ((_ride == null || _ride.Stage != RideStage.Seated) && _jStage == JStage.None) return true;   // on foot (also with a seat request in flight)
            _jTownBlocked++;
            if (_jTownBlocked <= 3 || _jTownBlocked % 50 == 0) _log.Info("TRAVEL menu not opened on the passenger's instance (only the driver travels) x" + _jTownBlocked);
            return false;
        }

        uint SendRide(ushort id, byte op)
        {
            var e = new StateEntry { Scope = StateScope.Session, ScopeArg = id, Key = WorldKeys.VehicleRide, Value = new[] { op } };
            return _world.SendProposal(e);
        }

        // viaDoor: the player opened an original door and used its SitCar trigger (AllowEnterVehicle); false = E next to the car
        void RequestRide(LocalCar lc, VehicleRecord r, bool viaDoor)
        {
            if (_ride != null || _take != null) return;                     // one request at a time
            if (!PassengerApi) { Quiet("Multiplayer: passenger seat unavailable"); return; }
            if (!lc.HasPassengerRoot || !lc.PassengerSeat.Valid) { Quiet("Multiplayer: this car has no passenger seat"); return; }
            _rideRequests++;
            if (_host)
            {
                string why;
                var code = _world.Host.Ride(_me, lc.Id, 1, _world.Clock, out why);
                _world.FlushOutbox();
                _log.Info("PASSENGER " + CarName(lc.Id) + " seat requested by the host -> " + code + (why != null ? " (" + why + ")" : ""));
                _ride = new Ride { Id = lc.Id, Car = lc, At = Now, Stage = RideStage.Requested, ViaDoor = viaDoor };
                RideResult(code, why);
                return;
            }
            if (!_node.IsConnected || !_world.Synced) { Quiet("Multiplayer: not connected to the host"); return; }
            uint id = SendRide(lc.Id, 1);
            _ride = new Ride { Id = lc.Id, Car = lc, ProposalId = id, At = Now, Stage = RideStage.Requested, ViaDoor = viaDoor };
            _log.Info("PASSENGER " + CarName(lc.Id) + " seat requested #" + id + " (" + r + ")");
        }

        void RideResult(ProposalCode code, string why)
        {
            if (_ride == null) return;
            if (code == ProposalCode.Accepted || code == ProposalCode.Unchanged)
            {
                _rideGranted++;
                _ride.Stage = RideStage.Granted; _ride.At = Now;
                return;
            }
            _rideDenied++;
            Quiet(code == ProposalCode.Busy ? "Multiplayer: the passenger seat is not available right now" + (why != null ? " (" + why + ")" : "") : "Multiplayer: the host refused the seat (" + code + ")");
            EndRide("refused: " + code);
        }

        void EndRide(string why)
        {
            if (_ride == null) return;
            _log.Info("PASSENGER " + CarName(_ride.Id) + " ride ended (" + why + ")");
            _ride = null;
            HideSelfBody();
            if (_players != null) _players.LocalSeated = false;
        }

        void ReleaseSeat(ushort id)
        {
            if (_host) { string why; _world.Host.Ride(_me, id, 0, _world.Clock, out why); _world.FlushOutbox(); return; }
            if (_node.IsConnected && _world.Synced) _ownIds.Add(SendRide(id, 0));
        }

        bool RideStillValid(Ride t, int region)
        {
            VehicleRecord r;
            var lc = t.Car;
            return Rec(t.Id, out r) && r.Passenger == _me && r.Region == region && r.Holder != _me && lc.Car != null && lc.Observed && lc.Active && t.Seat != null;
        }

        void RideFrame(double now, UnityEngine.Object ps, int region)
        {
            var t = _ride;
            var psc = ps as Component;
            switch (t.Stage)
            {
                case RideStage.Requested:
                    if (now - t.At > TakeTimeout) { Quiet("Multiplayer: the host did not answer, try again"); EndRide("host did not answer"); }
                    return;
                case RideStage.Granted:
                {
                    VehicleRecord r;
                    if (now - t.At > RideGrantSeconds) { ReleaseSeat(t.Id); EndRide("entry not possible after the grant (seat released)"); return; }
                    if (!Rec(t.Id, out r) || r.Passenger != _me) return;                      // registry delta not applied yet
                    var lc = t.Car;
                    if (lc.Car == null || !lc.Observed || !lc.Active || psc == null) return;
                    if (_psInVehicle(ps) || _psChase(ps)) { ReleaseSeat(t.Id); EndRide("player busy"); return; }
                    if (Vector3.Distance(psc.transform.position, lc.T.position) > 6f) { ReleaseSeat(t.Id); EndRide("player walked away"); return; }
                    // M4.3.2a: no door is animated for a proximity entry (the original game has no human rear-passenger entry
                    // animation; the companion simply takes associateSitPos). The seating transition is the original seat lerp.
                    EnterSeat(t, ps);
                    return;
                }
                case RideStage.Seated:
                {
                    if ((!RideStillValid(t, region) && !JointHolds(t.Id)) || psc == null) { ForceExitRide("seat or car no longer valid", true); return; }
                    var tr = psc.transform;
                    if (tr.parent != t.Seat) { ForceExitRide("the player was moved out of the seat by the game", true); return; }
                    tr.localPosition = Vector3.Lerp(tr.localPosition, Vector3.zero, Time.deltaTime * 3f);   // CarController.Update seat lerp (dt x 3)
                    // the door the player opened to use the SitCar trigger closes after 0.25 s like L_/R_Door_Close_Delay;
                    // a proximity entry touches no door (doors opened by the other player stay as they are)
                    if (!t.DoorsClosed && now - t.At >= 0.25) { t.DoorsClosed = true; if (t.ViaDoor) CloseDoorsLocal(t.Car); }
                    VehicleRecord r; Rec(t.Id, out r);
                    bool ui = UiBlocked(now, ps);
                    bool wantTpp = r.Tpp && r.Driver != 0;
                    if (wantTpp != t.Tpp && !ui && !SleepPresenter.Observing) SetPassengerCamera(t, ps, wantTpp);   // never while the original storage/pause UI owns the camera
                    SelfBody(t, t.Tpp);
                    SeatInteraction(now, t, ps, ui);
                    return;
                }
                case RideStage.Exiting:
                    if (now - t.At >= RideExitSeconds) { RestoreOnFoot(ps); _rideExits++; EndRide("got out"); }
                    return;
            }
        }

        Transform EnsureSeatAnchor(LocalCar lc)
        {
            if (lc.SeatAnchor != null) return lc.SeatAnchor;
            if (!lc.HasPassengerRoot || lc.T == null) return null;
            var go = new GameObject("TS2MP_PassengerSeat");
            go.transform.SetParent(lc.T, false);
            go.transform.localPosition = new Vector3(lc.PassengerRootPos.X, lc.PassengerRootPos.Y, lc.PassengerRootPos.Z);
            go.transform.localRotation = new Quaternion(lc.PassengerRootRot.X, lc.PassengerRootRot.Y, lc.PassengerRootRot.Z, lc.PassengerRootRot.W);
            lc.SeatAnchor = go.transform;
            return lc.SeatAnchor;
        }

        void EnterSeat(Ride t, UnityEngine.Object ps)
        {
            var lc = t.Car; var psc = (Component)ps;
            var seat = EnsureSeatAnchor(lc);
            if (seat == null) { ReleaseSeat(t.Id); EndRide("no passenger seat anchor"); return; }
            try { if (_hideWeapons != null) _hideWeapons(ps); } catch { }
            var pmb = _psPMB(ps);
            if (pmb != null) { if (_pmbSetCrouch != null) _pmbSetCrouch(pmb, false); try { if (_pmbHeight != null) _pmbHeight(pmb); } catch { } _pmbSetCanMove(pmb, false); }
            if (_psTorch != null && _setTorch != null && _psTorch(ps)) { var l = _psLight != null ? _psLight(ps) as GameObject : null; if (l != null) l.SetActive(false); _setTorch(ps, false); }
            var cc = _psCC(ps) as CharacterController; if (cc != null) cc.enabled = false;
            var cap = psc.GetComponent<CapsuleCollider>(); if (cap != null) cap.enabled = false;
            _setCanInteract(ps, false);
            psc.transform.SetParent(seat, true);
            psc.transform.localRotation = Quaternion.identity;
            var pcb = _psPCB(ps); if (pcb != null) { try { _pcbEnterRot(pcb, 0f); } catch { } }
            t.Seat = seat; t.Stage = RideStage.Seated; t.At = Now; t.DoorsClosed = false; t.Tpp = false;
            if (_players != null) _players.LocalSeated = true;
            _rideSeated++;
            if (_host) { string why; var c = _world.Host.Ride(_me, t.Id, 2, _world.Clock, out why); _world.FlushOutbox(); if (c == ProposalCode.NotAuthorized) { ForceExitRide("seat expired before entry", true); return; } }
            else t.ConfirmId = SendRide(t.Id, 2);
            _log.Info("PASSENGER entered " + CarName(t.Id) + " (seat " + lc.PassengerRootPos + ", car simulated by P" + _other + "): no driving authority");
        }

        // CarController.L_/R_Door_Close_Delay (IL): an open door is closed after entry (reported through the door sync).
        void CloseDoorsLocal(LocalCar lc)
        {
            if (!DoorApi || lc.Doors == null) return;
            for (int d = 0; d < VehicleDoors.Count; d++) { var o = lc.Doors[d]; if (o != null && _doorIsOpen(o) && !_doorLoadSlow(o) && !_doorSlowly(o)) SwingDoor(o, false); }
        }

        // CarController.Set_TPP (IL) camera part only: TPP = main camera off, PlayerCameraBehavior + HandCamera off, the shared
        // TPP_Camera_Controller follows this car, minimap follows the TPP camera; FP = the reverse. tpp_character (the
        // local driver model) and blockPassingTime are driver-only and untouched.
        void SetPassengerCamera(Ride t, UnityEngine.Object ps, bool tpp)
        {
            var car = t.Car.Car;
            var main = _carMainCam(car) as Component; var tppCam = _carTppCam(car);
            var pcb = _psPCB(ps) as Behaviour; var hand = _psHand(ps) as Behaviour;
            var gm = _world.GameManagerObject; var mm = gm != null && _gmMinimap != null ? _gmMinimap(gm) : null;
            try
            {
                if (tpp)
                {
                    if (main != null) main.gameObject.SetActive(false);
                    if (pcb != null) pcb.enabled = false;
                    if (hand != null) hand.enabled = false;
                    if (tppCam != null) _tppSetNewCar(tppCam, t.Car.T);
                    var co = tppCam != null && _tppCamObj != null ? _tppCamObj(tppCam) as Component : null;
                    if (mm != null && _mmSetTarget != null && co != null) _mmSetTarget(mm, co.gameObject);
                }
                else
                {
                    if (tppCam != null) _tppDisable(tppCam);
                    if (main != null) main.gameObject.SetActive(true);
                    if (hand != null) hand.enabled = true;
                    if (pcb != null) pcb.enabled = true;
                    var psc = ps as Component;
                    if (mm != null && _mmSetTarget != null && psc != null) _mmSetTarget(mm, psc.gameObject);
                }
            }
            catch (Exception ex) { _log.Warn("PASSENGER camera switch failed: " + ex.Message); }
            t.Tpp = tpp; _camSwitches++;
            _log.Info("PASSENGER camera -> " + (tpp ? "third person (original TPP vehicle camera on " + CarName(t.Id) + ")" : "first person (own seat)") + " following the driver's mode");
        }

        // The driver's camera mode on the instance that simulates the car: Set_TPP(true) deactivates main_camera.
        bool DriverTpp(LocalCar lc)
        {
            var main = _carMainCam != null ? _carMainCam(lc.Car) as Component : null;
            return main != null && !main.gameObject.activeInHierarchy;
        }

        // M4.3.2a: the passenger gets out beside its own seat (PassengerInteraction.ExitSpots: Ranger rear bench -> driver side,
        // behind the driver door; Shadowhawk front passenger seat -> right side), computed from the car's current transform.
        // No door is animated (none belongs to these seats in the original asset; doors opened by somebody else stay as they
        // are). A moving car or a blocked spot keeps the player seated (logged only).
        void TryExitRide(Ride t, UnityEngine.Object ps)
        {
            var psc = (Component)ps; var lc = t.Car;
            if (JointHolds(t.Id)) { _exitBlocked++; Quiet("passenger exit: joint journey in progress (stays seated)"); return; }   // M4.3 Phase B freeze
            if (!lc.Remote.Stopped(Now)) { _exitBlocked++; Quiet("passenger exit: the car is moving (stays seated)"); return; }
            Vector3 feet; int spot;
            if (!FindExitSpot(lc, psc.transform, out feet, out spot)) { _exitBlocked++; Quiet("passenger exit: no free spot beside the seat (stays seated)"); return; }
            if (t.Tpp) SetPassengerCamera(t, ps, false);
            HideSelfBody();
            float carYaw = lc.T.localEulerAngles.y;
            PlaceOutside(psc, feet);
            var pcb = _psPCB(ps); if (pcb != null) { try { _pcbExitRot(pcb, carYaw); } catch { } }
            var anim = _psCamAnim != null ? _psCamAnim(ps) as Animation : null;
            if (anim != null && anim.GetClip("GetOutCar") != null) anim.Play("GetOutCar");
            t.Stage = RideStage.Exiting; t.At = Now; t.Seat = null;
            if (_players != null) _players.LocalSeated = false;
            ReleaseSeat(t.Id);
            _log.Info("PASSENGER getting out of " + CarName(t.Id) + " at " + Fmt(feet) + " (" + (spot == 0 ? "beside the seat" : "mirrored side, primary blocked") + ", no door animated)");
        }

        void PlaceOutside(Component psc, Vector3 feet)
        {
            var tr = psc.transform;
            tr.SetParent(null, true);
            var cc = tr.GetComponent<CharacterController>();
            float below = cc != null ? Mathf.Max(0f, -(cc.center.y - cc.height * 0.5f) * Mathf.Abs(tr.lossyScale.y)) : 0f;
            tr.position = feet + Vector3.up * (below + 0.05f);
            var e = tr.eulerAngles; tr.rotation = Quaternion.Euler(0f, e.y, 0f);
        }

        // Exit spots (car-local, PassengerInteraction.ExitSpots): beside the passenger seat at the seat's z, just outside the
        // car's collider box; the mirrored side only if that is blocked (the rear bench spans both sides; for a side seat the
        // mirrored spot is across the car - still adjacent to the cabin). Each needs ground at the car's own ground level
        // (wheel contact, +-0.6 m) and a free player capsule (walls, the car body, other cars, traffic). None -> stay seated.
        bool FindExitSpot(LocalCar lc, Transform player, out Vector3 feet, out int spot)
        {
            feet = Vector3.zero; spot = -1;
            if (!lc.HasBounds || !lc.PassengerSeat.Valid) return false;
            var b = lc.LocalBounds;
            Vec3 a0, a1;
            PassengerInteraction.ExitSpots(lc.PassengerSeat.Pos, DriverSideX(lc), b.min.x, b.max.x, out a0, out a1);
            for (int i = 0; i < 2; i++)
            {
                var v = i == 0 ? a0 : a1;
                var c = lc.T.TransformPoint(new Vector3(v.X, lc.GroundY + 1.0f, v.Z));
                Vector3 f;
                if (!GroundAt(c, player, out f)) continue;
                float fl = lc.T.InverseTransformPoint(f).y - lc.GroundY;
                if (fl < -PassengerInteraction.GroundBelow || fl > PassengerInteraction.GroundAbove) continue;   // another level / kerb too high
                const float r = 0.35f;
                int n = Physics.OverlapCapsuleNonAlloc(f + Vector3.up * (r + 0.1f), f + Vector3.up * (1.8f - r), r, _cols, ~0, QueryTriggerInteraction.Ignore);
                bool blocked = false;
                for (int k = 0; k < n; k++) if (_cols[k] != null && !_cols[k].transform.IsChildOf(player)) { blocked = true; break; }
                if (blocked) continue;
                feet = f; spot = i; return true;
            }
            return false;
        }

        // Driver side in car-local x: the original driver door (car_door_L), else the original driver exit point.
        float DriverSideX(LocalCar lc)
        {
            var dl = _carDoorL != null ? _carDoorL(lc.Car) as Component : null;
            if (dl != null) return lc.T.InverseTransformPoint(dl.transform.position).x;
            var leave = _carLeave(lc.Car) as Transform;
            return leave != null ? lc.T.InverseTransformPoint(leave.position).x : -1f;
        }

        // ExitCar's tail (IL): CharacterController + CapsuleCollider on, PlayerMotorBehavior.canMove, PCB rotation allowed,
        // interaction back after InteractDelayed(0.8).
        void RestoreOnFoot(UnityEngine.Object ps)
        {
            var psc = ps as Component;
            if (psc == null) return;
            var cc = _psCC(ps) as CharacterController; if (cc != null) cc.enabled = true;
            var cap = psc.GetComponent<CapsuleCollider>(); if (cap != null) cap.enabled = true;
            var pmb = _psPMB(ps); if (pmb != null) _pmbSetCanMove(pmb, true);
            try { _interactDelayed(ps, 0.8f); } catch { _setCanInteract(ps, true); }
        }

        // Any invalid state while seated (car becomes simulated here or disappears, host cleared the seat, region change,
        // disconnect): restore a complete on-foot player at once, outside the car when possible, and free the seat.
        void ForceExitRide(string why, bool releaseSeat)
        {
            var t = _ride;
            if (t == null) return;
            _ride = null;
            HideSelfBody();
            if (_players != null) _players.LocalSeated = false;
            if (t.Stage == RideStage.Requested || t.Stage == RideStage.Granted) { if (releaseSeat && t.Stage == RideStage.Granted) ReleaseSeat(t.Id); _log.Info("PASSENGER request for " + CarName(t.Id) + " dropped (" + why + ")"); return; }
            _rideForced++;
            var gm = _world.GameManagerObject; var ps = gm != null ? _gmPS(gm) : null; var psc = ps as Component;
            try
            {
                if (psc != null)
                {
                    if (t.Tpp) SetPassengerCamera(t, ps, false);
                    if (t.Seat != null && psc.transform.parent == t.Seat)
                    {
                        Vector3 feet = Vector3.zero;
                        int which; bool spot = t.Car.Car != null && t.Car.Active && FindExitSpot(t.Car, psc.transform, out feet, out which);
                        if (spot) PlaceOutside(psc, feet); else psc.transform.SetParent(null, true);
                    }
                    RestoreOnFoot(ps);
                }
            }
            catch (Exception ex) { _log.Error("PASSENGER forced exit restore failed", ex); }
            if (releaseSeat) ReleaseSeat(t.Id);
            _log.Warn("PASSENGER left " + CarName(t.Id) + " immediately (" + why + "): on-foot controls restored");
        }

        // ======================================================================== M4.3.1 interaction resolver
        // One E press = one action. Order: (1) the original interaction (PlayerScript.Update casts its ray and runs the tag
        // branch itself; the mod only classifies the same ray and steps back), (2) the seated passenger's own input (storage
        // bridge / out of reach / exit), (3) the multiplayer passenger request, (4) nothing. Evaluated in Update from the same
        // ray PlayerScript.Update casts (cam, forward, 2.25 m, PlayerScript.mask), only while a passenger-eligible car is within
        // a few metres (on foot) or while seated - no scene search, no allocation.
        double _uiFreeAt;
        bool _entryHint;                 // LateUpdate shows the original CarEnterInfo prompt (PlayerScript.Update hides it every frame)

        // The original inventory/storage/shop or pause UI is open (Time.timeScale 0, Inventory.isStorage / isShop) or was closed
        // less than 0.35 s ago: the E that closed it must not also get out / request a seat.
        bool UiBlocked(double now, UnityEngine.Object ps)
        {
            bool open = Time.timeScale <= 0f;
            if (!open && _psInv != null && _invStorage != null && _invShop != null)
            {
                var inv = _psInv(ps);
                if (inv != null) open = _invStorage(inv) || _invShop(inv);
            }
            if (open) _uiFreeAt = now + 0.35;
            return open || now < _uiFreeAt;
        }

        bool OriginalRay(UnityEngine.Object ps, out RaycastHit hit, out Transform cam)
        {
            hit = default(RaycastHit);
            var c = _psCam(ps) as Component;
            cam = c != null ? c.transform : null;
            if (cam == null) return false;
            return Physics.Raycast(cam.position, cam.TransformDirection(Vector3.forward), out hit, PassengerInteraction.RayLength, (int)_psMask(ps));
        }

        InteractTarget Classify(bool hit, RaycastHit h, LocalCar car)
        {
            if (!hit || h.collider == null) return InteractTarget.None;
            var go = h.collider.gameObject;
            bool carBox = _carBoxT != null && go.GetComponent(_carBoxT) != null;
            bool medkit = _medkitT != null && go.GetComponent(_medkitT) != null;
            bool ofCar = car != null && h.collider.transform.IsChildOf(car.T);
            return PassengerInteraction.Classify(true, h.transform != null ? h.transform.tag : null, carBox, medkit, ofCar);
        }

        // ---- on foot: E next to a car the other player drives requests the passenger seat ----
        // M4.3.2a: evaluated at 10 Hz for the prompt and freshly on the frame E goes down (0.6.1 host log: our update cost rose
        // from ~60 to 150-190 us avg while probing every frame).
        double _nextFootProbe, _nextSeatProbe;
        InteractTarget _seatRay;
        void FootFrame(double now, UnityEngine.Object ps)
        {
            if (!PassengerApi || !ResolverApi || ps == null) { _entryHint = false; return; }
            var psc = ps as Component;
            if (psc == null) { _entryHint = false; return; }
            bool pressed = false; try { pressed = _interact(ps); } catch { }
            if (!pressed && !_entryHint && now < _nextFootProbe) return;   // every frame while the prompt is shown (hides at once)
            _nextFootProbe = now + 0.1;
            bool wasShown = _entryHint;
            _entryHint = false;
            // nearest observed car within 5 m that the other player drives (cheap: <= 2 bound cars)
            LocalCar car = null; VehicleRecord rec = default(VehicleRecord); float best = 25f;
            var pp = psc.transform.position;
            for (int i = 0; i < _local.Count; i++)
            {
                var lc = _local[i]; VehicleRecord r;
                if (!lc.Observed || !lc.Active || !lc.HasBounds || !Rec(lc.Id, out r) || r.Driver != _other || r.Driver == 0) continue;
                float d2 = (lc.T.position - pp).sqrMagnitude;
                if (d2 < best) { best = d2; car = lc; rec = r; }
            }
            if (car == null) return;
            bool ui = UiBlocked(now, ps);
            var p = new FootProbe();
            p.WasShown = wasShown;
            p.Gates = !ui && OnFootGates(ps);
            if (!p.Gates) return;
            RaycastHit h; Transform cam;
            bool hit = OriginalRay(ps, out h, out cam);
            if (cam == null) return;
            p.Ray = Classify(hit, h, car);
            var act = PresentationRules.Enter(car.Pres, rec, _me, car.Remote.Settled(now), car.Remote.Stopped(now));
            p.CarEligible = act == EnterAction.RequestPassenger && car.HasPassengerRoot && car.PassengerSeat.Valid && _ride == null && _take == null;
            Vector3 feet = FeetOf(psc);
            var l = car.T.InverseTransformPoint(feet); var b = car.LocalBounds;
            float dx = Mathf.Max(b.min.x - l.x, 0f, l.x - b.max.x), dz = Mathf.Max(b.min.z - l.z, 0f, l.z - b.max.z);
            p.Reach = Mathf.Sqrt(dx * dx + dz * dz);
            p.FeetHeight = l.y - car.GroundY;
            if (p.CarEligible && PassengerInteraction.NearCar(p.Reach, p.FeetHeight, p.WasShown) && p.Ray != InteractTarget.Native && p.Ray != InteractTarget.CarBox && p.Ray != InteractTarget.Medkit)
                p.LineOfSight = ClearLineToCar(car, cam.position, l, psc.transform);
            var action = PassengerInteraction.OnFoot(p);
            _entryHint = action == FootAction.RequestPassenger;
            if (!pressed) return;
            if (action == FootAction.RequestPassenger)
            {
                _footEntries++;
                _log.Info("PASSENGER entry by E next to " + CarName(car.Id) + " (ray " + p.Ray + ", reach " + p.Reach.ToString("0.00") + " m, feet " + p.FeetHeight.ToString("0.00") + " m above the car's ground)");
                RequestRide(car, rec, false);
            }
            else if (PassengerInteraction.NearCar(p.Reach, p.FeetHeight, false) && action != FootAction.Original)
                Quiet("passenger entry not possible: " + act + (p.CarEligible && !p.LineOfSight ? " (no clear line to the car)" : ""));
        }

        // Accessibility: a straight line from the player's eyes to the nearest point of the car's box (at the eyes' height,
        // clamped into the box) reaches the car or nothing - a wall, fence or another object in between blocks it. Triggers and
        // the player's own colliders are ignored.
        bool ClearLineToCar(LocalCar car, Vector3 eye, Vector3 feetLocal, Transform player)
        {
            var b = car.LocalBounds;
            var el = car.T.InverseTransformPoint(eye);
            var near = car.T.TransformPoint(new Vector3(Mathf.Clamp(feetLocal.x, b.min.x, b.max.x), Mathf.Clamp(el.y, b.min.y, b.max.y), Mathf.Clamp(feetLocal.z, b.min.z, b.max.z)));
            var dir = near - eye; float len = dir.magnitude;
            if (len < 1e-3f) return true;
            int n = Physics.RaycastNonAlloc(eye, dir / len, _hits, len, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = _hits[i].collider;
                if (c == null || c.transform.IsChildOf(player) || c.transform.IsChildOf(car.T)) continue;
                return false;
            }
            return true;
        }

        // The gates of the original E ray branch (PlayerScript.Update IL_46a3..IL_4760) that matter on foot.
        bool OnFootGates(UnityEngine.Object ps)
        {
            if (Time.timeScale <= 0f || _psInVehicle(ps) || !_psCanInteract(ps) || _psCarrying(ps) || _psHiding(ps) || _psSlowDoor(ps) || _psCloseUp(ps) || _psHideHands(ps)) return false;
            var pmb = _psPMB(ps) as Behaviour; var pcb = _psPCB(ps) as Behaviour;
            return pmb != null && pmb.enabled && !_pmbRunning(pmb) && pcb != null && pcb.enabled;
        }

        Vector3 FeetOf(Component psc)
        {
            var cc = _psCC(psc) as CharacterController;
            if (cc != null && cc.enabled) { var bb = cc.bounds; return new Vector3(bb.center.x, bb.min.y, bb.center.z); }
            return psc.transform.position;
        }

        // LateUpdate runs after this frame's coroutines (Unity order: Update -> yield null / WaitForSeconds -> LateUpdate).
        // (1) Seated passenger: PlayerScript.DisableInventory (closing the inventory / storage / pause UI) calls
        //     InteractDelayed, whose coroutine sets canInteract = true while the player still sits in the car; with it the
        //     original E branch (medkit, doors, pickups) came back from the passenger seat (M4.3.2a root cause of the medkit
        //     report). It is kept off for the whole ride, before the next PlayerScript.Update; the exit restores it.
        // (2) PlayerScript.Update hides CarEnterInfo only inside its ray-hit branch (IL_772b), so the mod hides it again itself
        //     when its own hint ends; the original re-shows it next frame if the player aims at the SitCar trigger.
        bool _hintShown;
        public void LateUpdate()
        {
            if (!_ok) return;
            var t = _ride;
            if (t == null && !_entryHint && !_hintShown) return;
            var gm = _world.GameManagerObject; var ps = gm != null ? _gmPS(gm) : null;
            if (ps == null) return;
            if (t != null && t.Stage == RideStage.Seated && _psCanInteract != null && _psCanInteract(ps))
            {
                _setCanInteract(ps, false); _canInteractKept++;
                if (!t.KeptInteract) { t.KeptInteract = true; _log.Info("PASSENGER the game re-enabled interaction while seated (closing a UI): kept off until the exit"); }
            }
            if (!_entryHint && !_hintShown) return;
            var info = _psEnterInfo != null ? _psEnterInfo(ps) as GameObject : null;
            if (info == null) { _hintShown = false; return; }
            if (_entryHint)
            {
                if (!info.activeSelf) info.SetActive(true);
                // M4.3.2b root cause of the missing prompt (0.6.2 client log: every entry "ray None"): when the original ray hits
                // nothing, PlayerScript.Update (IL_7f5a) fades alphaInteractPrompts to 0, hiding CarEnterInfo. The passenger hint
                // keeps that canvas visible and switches off the original target prompts the ray branch would have reset (no
                // original target is focused, otherwise the hint would be false).
                var cg = _psPrompts != null ? _psPrompts(ps) as CanvasGroup : null;
                if (cg != null && cg.alpha < 1f)
                {
                    for (int i = 0; i < _staleTargetPrompts.Count; i++) { var g = _staleTargetPrompts[i](ps) as GameObject; if (g != null && g.activeSelf) g.SetActive(false); }
                    cg.alpha = 1f;
                }
                if (!_hintShown) { _hintShown = true; _hintShows++; }
            }
            else { info.SetActive(false); _hintShown = false; }
        }

        // ---- seated: storage bridge, medkit out of reach, else exit ----
        void SeatInteraction(double now, Ride t, UnityEngine.Object ps, bool ui)
        {
            var sp = new SeatProbe { Gates = !ui && now - t.At > 1.0 && Time.timeScale > 0f && !SleepPresenter.Observing, FirstPerson = !t.Tpp };
            bool pressed = false;
            if (sp.Gates) { try { pressed = _interact(ps); } catch { } }
            if (sp.FirstPerson && ResolverApi && (pressed || now >= _nextSeatProbe))
            {
                _nextSeatProbe = now + 0.1;
                _seatRay = SeatTarget(t.Car, ps);
                bool hostBox = _seatRay == InteractTarget.CarBox && PassengerInteraction.SeatStorageAllowed(_host, CarBoxSync.Ready) && _psOpenStorage != null && _carBoxType != null;
                if (hostBox && _carBoxHl != null && _hlSet != null && t.Car.CarBox != null) { var hl = _carBoxHl(t.Car.CarBox); if (hl != null) try { _hlSet(hl, true); } catch { } }   // CarBox.Update clears it after 0.15 s
            }
            else if (!sp.FirstPerson) _seatRay = InteractTarget.None;
            if (!pressed) return;
            sp.Ray = _seatRay;
            switch (PassengerInteraction.Seated(sp))
            {
                case SeatAction.OpenStorage:
                    if (!PassengerInteraction.SeatStorageAllowed(_host, CarBoxSync.Ready) || _psOpenStorage == null || _carBoxType == null)
                    {
                        // the client's car storage is an unshared sandbox copy: nothing happens until the shared CarBox (M4.3.2b)
                        _storageRefused++;
                        Quiet("passenger storage on the client: not shared yet (M4.3.2b), nothing opened");
                        return;
                    }
                    var inv = _psInv(ps);
                    if (inv != null && _invShop(inv)) return;                              // original CarBox branch: !invScript.isShop
                    int type = _carBoxType(t.Car.CarBox);
                    _storageOpens++;
                    _log.Info("PASSENGER opened the car storage from the seat (original PlayerScript.OpenStorage(" + type + "))");
                    try { _psOpenStorage(ps, type); } catch (Exception ex) { _log.Warn("PASSENGER OpenStorage failed: " + ex.Message); }
                    _uiFreeAt = now + 0.35;
                    return;
                case SeatAction.OutOfReach:
                    _medkitRefused++;
                    Quiet("passenger aimed at the medkit: out of reach from the seat, nothing happens");
                    return;
                case SeatAction.Exit:
                    TryExitRide(t, ps);
                    return;
            }
        }

        // What the passenger looks at: the original ray (medkit, door, other colliders) and the recorded CarBox box (its collider
        // is disabled on a car simulated elsewhere), whichever is nearer within the original 2.25 m. The CarBox counts only
        // when no solid non-car collider is in front of it.
        InteractTarget SeatTarget(LocalCar lc, UnityEngine.Object ps)
        {
            RaycastHit h; Transform cam;
            bool hit = OriginalRay(ps, out h, out cam);
            if (cam == null) return InteractTarget.None;
            var target = Classify(hit, h, lc);
            float hitDist = hit ? h.distance : float.MaxValue;
            float boxDist;
            // the car's own plain body/interior colliders do not hide the box (the driver reaches it with the original ray); the
            // medkit, other original interactables or anything outside the car in front of it do
            if (lc.HasCarBox && RayToCarBox(lc, cam.position, cam.forward, out boxDist) && boxDist <= PassengerInteraction.RayLength
                && (!hit || boxDist <= hitDist || target == InteractTarget.CarBody)) return InteractTarget.CarBox;
            return target;
        }

        bool RayToCarBox(LocalCar lc, Vector3 origin, Vector3 dir, out float dist)
        {
            dist = 0f;
            Transform space; Bounds box;
            if (lc.CarBoxBox != null) { space = lc.CarBoxBox.transform; box = new Bounds(lc.CarBoxBox.center, lc.CarBoxBox.size); }
            else { space = lc.T; box = lc.CarBoxLocal; }
            var lo = space.InverseTransformPoint(origin); var ld = space.InverseTransformDirection(dir);
            float ldist;
            if (!box.IntersectRay(new Ray(lo, ld), out ldist)) return false;
            dist = Vector3.Distance(origin, space.TransformPoint(lo + ld.normalized * ldist));
            return true;
        }

        // ---- local passenger body (third person) ----
        // The local player has no full-body model (first-person arms/legs only; the driver's third-person body is the car's own
        // tpp_character, driver seat only). In third person the passenger therefore sees the same original associate model
        // (Codename/anim, Sitting clip) at the same anchor (associateSitPos, Codename root) the other player sees: one
        // visual-only clone without scripts/colliders (RemoteAvatar), never shown in first person, hidden on every exit path.
        RemoteAvatar _selfBody;
        void SelfBody(Ride t, bool show)
        {
            var lc = t.Car;
            if (!PassengerInteraction.SelfBodyVisible(t.Stage == RideStage.Seated, show, lc.T != null && lc.PassengerSeat.Valid)) { HideSelfBody(); return; }
            if (_selfBody == null)
            {
                if (!RemoteAvatar.TryEnsureTemplate(_log)) return;
                _selfBody = RemoteAvatar.Spawn("LocalPassenger", _log, false);
                if (_selfBody == null) return;
            }
            var s = lc.PassengerSeat;
            var pos = lc.T.TransformPoint(new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z));
            var rot = lc.T.rotation * new Quaternion(s.Rot.X, s.Rot.Y, s.Rot.Z, s.Rot.W);
            _selfBody.ApplySeated(pos, rot, s.CodenameRoot);
            if (!_selfBody.Visible) { _selfBody.SetVisible(true); _selfShown++; _log.Info("PASSENGER own body shown (third person, original associate model at " + s + ")"); }
        }

        void HideSelfBody()
        {
            if (_selfBody != null && _selfBody.Visible) { _selfBody.SetVisible(false); _log.Info("PASSENGER own body hidden"); }
        }
        long _selfShown;

        static bool Inside(Transform t, Bounds local, Vector3 world)
        {
            var l = t.InverseTransformPoint(world);
            var b = local; b.Expand(1.0f);
            return b.Contains(l);
        }

        // ---- M4.2.2 seated remote driver ----
        // Two passes over the bound cars: (1) read the car-local seat transforms, learn the seated Codename-root height below
        // the first-person sit root from any car with a front-row associate seat; (2) choose each car's driver anchor and
        // apply the optional manual calibration (config Vehicles.DriverSeatAdjust).
        void ProbeSeats()
        {
            int n = _local.Count;
            var has = new bool[n * 3]; var pos = new Vec3[n * 3]; var rot = new Quat[n * 3]; var names = new string[n * 3];
            float k = float.NaN;
            for (int i = 0; i < n; i++)
            {
                var lc = _local[i];
                try
                {
                    var ts = new Transform[] { _assocT != null ? _assocT(lc.Car) as Transform : null, _sitT != null ? _sitT(lc.Car) as Transform : null, null };
                    var tppGo = _tppChar != null ? _tppChar(lc.Car) as GameObject : null; ts[2] = tppGo != null ? tppGo.transform : null;
                    for (int j = 0; j < 3; j++)
                    {
                        if (ts[j] == null) continue;
                        has[i * 3 + j] = true; pos[i * 3 + j] = Local(lc.T, ts[j].position); rot[i * 3 + j] = LocalRot(lc.T, ts[j].rotation); names[i * 3 + j] = ts[j].name;
                    }
                    float ki = SeatRules.CodenameBelowSit(has[i * 3], pos[i * 3], has[i * 3 + 1], pos[i * 3 + 1]);
                    if (!float.IsNaN(ki) && float.IsNaN(k)) k = ki;
                }
                catch (Exception ex) { _log.Warn("SEAT_PROBE failed for " + CarName(lc.Id) + ": " + ex.Message); }
            }
            var adjust = SeatRules.ParseAdjust(_cfg.DriverSeatAdjust);
            var passengerAdjust = SeatRules.ParseAdjust(_cfg.PassengerSeatAdjust);
            for (int i = 0; i < n; i++)
            {
                var lc = _local[i];
                var spec = SeatRules.Driver(has[i * 3], pos[i * 3], rot[i * 3], has[i * 3 + 1], pos[i * 3 + 1], has[i * 3 + 2], pos[i * 3 + 2], rot[i * 3 + 2], k);
                Vec3 d; bool adj = adjust.TryGetValue(lc.Kind, out d);
                if (adj) spec = SeatRules.Adjust(spec, d);
                lc.Seat = spec;
                lc.PassengerSeat = SeatRules.Passenger(has[i * 3], pos[i * 3], rot[i * 3]);
                lc.HasPassengerRoot = has[i * 3];
                if (lc.HasPassengerRoot)
                {
                    lc.PassengerRootPos = SeatRules.PassengerRoot(pos[i * 3], has[i * 3 + 1], pos[i * 3 + 1], k);
                    lc.PassengerRootRot = has[i * 3 + 1] ? rot[i * 3 + 1] : rot[i * 3];
                    Vec3 pd; if (passengerAdjust.TryGetValue(lc.Kind, out pd)) lc.PassengerRootPos = new Vec3(lc.PassengerRootPos.X + pd.X, lc.PassengerRootPos.Y + pd.Y, lc.PassengerRootPos.Z + pd.Z);
                }
                if (_warned.Add(71000 + lc.Kind))
                    _log.Info("SEAT_PROBE " + CarName(lc.Id) + " passenger: remote avatar " + (lc.PassengerSeat.Valid ? lc.PassengerSeat.ToString() : "none") + " (original associateSitPos, Codename root)"
                        + "; local first-person root " + (lc.HasPassengerRoot ? lc.PassengerRootPos.ToString() : "none") + (passengerAdjust.ContainsKey(lc.Kind) ? " (manual adjust)" : " (derived, needs in-game calibration)"));
                if (_warned.Add(70000 + lc.Kind))
                    _log.Info("SEAT_PROBE " + CarName(lc.Id) + " (car-local): associateSitPos=" + (has[i * 3] ? names[i * 3] + "@" + pos[i * 3] : "-") + " sit_transform=" + (has[i * 3 + 1] ? names[i * 3 + 1] + "@" + pos[i * 3 + 1] : "-")
                        + " tpp_character=" + (has[i * 3 + 2] ? names[i * 3 + 2] + "@" + pos[i * 3 + 2] : "-") + " codenameBelowSit=" + (float.IsNaN(k) ? "?" : k.ToString("0.00"))
                        + (adj ? " manualAdjust=" + d : "") + " -> driver anchor " + (spec.Valid ? spec.ToString() : "none (remote driver hidden)"));
            }
        }

        static Vec3 Local(Transform car, Vector3 world) { var l = car.InverseTransformPoint(world); return new Vec3(l.x, l.y, l.z); }
        static Quat LocalRot(Transform car, Quaternion world) { var q = Quaternion.Inverse(car.rotation) * world; return new Quat(q.x, q.y, q.z, q.w); }

        // Runs in Update after Rigidbody interpolation placed the observed car, so the seated avatar never lags its car.
        void SeatFrame()
        {
            if (_players == null || !_players.RemoteSeatRequested) { SetSeatState(0, 0, SeatRole.None, SeatSource.None); return; }
            ushort id; SeatRole role;
            bool found = _view.FindSeat(_other, _region, out id, out role);
            Transform root = null; LocalCar spec = null;
            if (found)
                foreach (var lc in _local)
                {
                    if (lc.Id != id) continue;
                    spec = lc;
                    if (lc.Active && (lc.Observed || (lc.Mine && !lc.IsProxy))) root = lc.T;   // M4.3: a remote passenger sits in the car simulated here
                }
            var seatSpec = spec == null ? default(SeatSpec) : role == SeatRole.Passenger ? spec.PassengerSeat : spec.Seat;
            bool anchor = root != null && seatSpec.Valid;
            var view = SeatRules.Occupant(true, found, role, anchor);
            if (view != OccupantView.Seated) { SetSeatState(found ? 2 : 3, id, role, SeatSource.None); return; }
            var s = seatSpec;
            var pos = root.TransformPoint(new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z));
            var rot = root.rotation * new Quaternion(s.Rot.X, s.Rot.Y, s.Rot.Z, s.Rot.W);
            _players.PoseSeated(pos, rot, s.CodenameRoot);
            _seatPosed++;
            SetSeatState(1, id, role, s.Source);
        }

        // state: 0 none, 1 seated, 2 hidden (seat known, no anchor/presentation), 3 hidden (car not shared). Text only on change.
        int _seatKey;
        void SetSeatState(int state, ushort id, SeatRole role, SeatSource src)
        {
            int key = state | (id << 4) | ((int)role << 20) | ((int)src << 24);
            if (key == _seatKey) return;
            _seatKey = key;
            string s = state == 0 ? "-" : state == 1 ? (role == SeatRole.Passenger ? "passenger of " : "driver of ") + CarName(id) + " (" + src + ")"
                : state == 2 ? "hidden (" + role + " of " + CarName(id) + ", no anchor/presentation)" : "hidden (car not shared here)";
            _log.Info("REMOTE_SEAT " + _seatState + " -> " + s);
            _seatState = s;
        }

        // Host: freshest pose/fuel of a car for a handoff (own physics, else the last snapshot of its holder).
        readonly Dictionary<ushort, KeyValuePair<double, VehicleSnapshot>> _lastSnap = new Dictionary<ushort, KeyValuePair<double, VehicleSnapshot>>();
        bool LivePark(ushort id, out ParkRecord p)
        {
            p = default(ParkRecord);
            foreach (var lc in _local)
            {
                if (lc.Id != id || !lc.Mine || !lc.Active || lc.IsProxy) continue;
                var gm = _world.GameManagerObject; var ps = gm != null ? _gmPS(gm) : null;
                if (lc == _fuelCar && ps != null) lc.Fuel = ReadGas(ps);
                var pos = lc.T.position; var rot = lc.T.rotation;
                p = new ParkRecord { Region = _region, HasPose = true, Pos = new Vec3(pos.x, pos.y, pos.z), Rot = new Quat(rot.x, rot.y, rot.z, rot.w), Fuel = lc.Fuel >= 0 ? Math.Min(lc.Fuel, TravelRules.MaxFuel) : -1f };
                return true;
            }
            KeyValuePair<double, VehicleSnapshot> ls;
            if (!_lastSnap.TryGetValue(id, out ls) || Now - ls.Key > 3.0) return false;
            var sn = ls.Value;
            p = new ParkRecord { Region = sn.Region, HasPose = true, Pos = sn.Pos, Rot = sn.Rot, Fuel = sn.Fuel };
            return true;
        }

        void OpenTravelMenu(LocalCar lc)
        {
            var gm = _world.GameManagerObject; var ps = gm != null ? _gmPS(gm) : null;
            if (ps == null) return;
            if (_psOpened(ps) || _psCaught(ps)) return;   // same guards as PlayerScript.OpenTownSelectionNoDelay
            _setPsOpened(ps, true);
            var psc = ps as MonoBehaviour;
            var co = _showTown(ps, 0f);
            if (psc == null || co == null) { _setPsOpened(ps, false); return; }
            psc.StartCoroutine(co);
            _proxyMenus++;
            _log.Info("TRAVEL menu opened from the hideout travel proxy of " + CarName(lc.Id) + " (" + lc.Pres + "): the car itself is not brought along");
        }

        // ---- per-car fuel: SaveGame.gasTankLeft belongs to the car this instance drives / holds ----
        float ReadGas(UnityEngine.Object ps) { var sg = _psSave(ps); return sg != null ? _gas(sg) : -1f; }
        void WriteGas(UnityEngine.Object ps, float v) { var sg = _psSave(ps); if (sg != null && v >= 0) _setGas(sg, v); }

        void FuelFrame(UnityEngine.Object ps)
        {
            if (ps == null) return;
            var cur = _psVehicle(ps) as Component;
            LocalCar want = null;
            foreach (var lc in _local) if (lc.Mine && lc.Active && !lc.IsProxy && ReferenceEquals(lc.Car, cur)) want = lc;
            if (want == null && _fuelCar != null && _fuelCar.Mine && _fuelCar.Active) want = _fuelCar;
            if (want == null) foreach (var lc in _local) if (lc.Mine && lc.Active && !lc.IsProxy) { want = lc; break; }
            if (want != _fuelCar)
            {
                float gas = ReadGas(ps);
                var old = _fuelCar;
                if (old != null) old.Fuel = gas;
                if (want != null) { if (want.Fuel >= 0) WriteGas(ps, want.Fuel); else want.Fuel = gas; }
                _log.Info("FUEL tank now belongs to " + (want != null ? CarName(want.Id) + " (" + want.Fuel.ToString("0.0") + ")" : "no car") + (old != null ? "; " + CarName(old.Id) + " keeps " + old.Fuel.ToString("0.0") : ""));
                _fuelCar = want;
            }
            if (_fuelCar != null) _fuelCar.Fuel = ReadGas(ps);
        }

        // ======================================================================== streaming held cars (both roles)
        void StreamFrame(double now, UnityEngine.Object ps, int region)
        {
            var cur = ps != null ? _psVehicle(ps) as Component : null;
            for (int i = 0; i < _local.Count; i++)
            {
                var lc = _local[i];
                if (lc.Car == null) { _local.Clear(); _boundGm = null; _fuelCar = null; return; }   // destroyed with its scene: rebind
                if (!lc.Mine || !lc.Active || lc.IsProxy) { if (lc.LastDriver != 0) DriverChange(lc, 0, region); continue; }
                bool engine = _engine(lc.Car);
                byte driver = ReferenceEquals(cur, lc.Car) && _sitting(lc.Car) ? _me : (byte)0;
                if (driver != lc.LastDriver) DriverChange(lc, driver, region);
                var p = lc.T.position; var r = lc.T.rotation;
                var pos = new Vec3(p.x, p.y, p.z); var rot = new Quat(r.x, r.y, r.z, r.w);
                float speed = lc.Rb != null ? Mathf.Max(lc.Rb.velocity.magnitude, lc.Rb.angularVelocity.magnitude * 1.5f) : 0f;
                bool moving = lc.Latch.Update(speed, now);
                bool tpp = driver != 0 && DriverTpp(lc);
                var flags = (engine ? VehicleStateFlags.EngineOn : VehicleStateFlags.None) | (driver != 0 ? VehicleStateFlags.Driven : VehicleStateFlags.None)
                    | (moving ? VehicleStateFlags.Moving : VehicleStateFlags.None) | (tpp ? VehicleStateFlags.Tpp : VehicleStateFlags.None);
                float fuel = lc.Fuel >= 0 ? Math.Min(lc.Fuel, TravelRules.MaxFuel) : 0f;
                float steer = 0f;
                if (_evpSteer != null && _drivetrain != null) { var dtr = _drivetrain(lc.Car); if (dtr != null) { try { steer = _evpSteer(dtr); } catch { steer = 0f; } } }
                if (_host)
                {
                    var fleet = _world.Host.Fleet;
                    _world.Host.Motion.Report(lc.Id, moving, driver != 0, _world.Clock);
                    if (engine != lc.LastEngine && fleet.SetEngine(lc.Id, engine, _world.Host.Outbox)) _commits++;
                    if (fleet.SetTpp(lc.Id, tpp, _world.Host.Outbox)) { _commits++; _world.FlushOutbox(); }
                    if (lc.Park.ShouldCommit(now, pos, rot, fuel, (byte)flags)
                        && fleet.SetPark(lc.Id, new ParkRecord { Region = region, HasPose = true, Pos = pos, Rot = rot, Fuel = lc.Fuel >= 0 ? fuel : -1f }, _world.Host.Outbox)) _commits++;
                }
                lc.LastEngine = engine;
                if (!_node.IsConnected || !lc.Policy.ShouldSend(now, pos, rot, flags, region)) continue;
                uint seq; _seqById.TryGetValue(lc.Id, out seq); _seqById[lc.Id] = ++seq;
                var s = new VehicleSnapshot { Id = lc.Id, Seq = seq, TimeMs = (uint)(now * 1000.0), Region = region, Pos = pos, Rot = rot, Flags = flags, Fuel = fuel, Steer = steer };
                _w.Reset(); s.Write(_w);
                _node.Send(_w, DeliveryMethod.Sequenced, SessionNode.ChannelVehicle);
                _sent++; _sentBytes += _w.Length;
            }
        }

        void DriverChange(LocalCar lc, byte driver, int region)
        {
            lc.LastDriver = driver;
            if (_host)
            {
                var fleet = _world.Host.Fleet;
                var code = driver != 0 ? fleet.Claim(lc.Id, driver, region, _world.Host.Outbox) : fleet.Release(lc.Id, _me, _world.Host.Outbox);
                if (code == ProposalCode.Accepted) _commits++;
                _log.Info("VEHICLE " + CarName(lc.Id) + (driver != 0 ? " driver lease P" + _me + " -> " : " driver released -> ") + code);
                return;
            }
            if (!_node.IsConnected || !_world.Synced) return;
            var e = new StateEntry { Scope = StateScope.Session, ScopeArg = lc.Id, Key = WorldKeys.VehicleClaim, Value = new[] { driver != 0 ? (byte)1 : (byte)0 } };
            uint id = _world.SendProposal(e);
            _ownIds.Add(id);
            _log.Info("VEHICLE " + CarName(lc.Id) + (driver != 0 ? " driver lease requested #" : " driver release sent #") + id);
        }

        // ======================================================================== remote snapshots
        void OnMessage(NetPeer peer, byte type, NetPacketReader r)
        {
            if (type != (byte)MsgType.VehicleState) return;
            _recv++; _recvBytes += r.AvailableBytes + 1;
            VehicleSnapshot s;
            if (!VehicleSnapshot.TryRead(r, out s)) { _malformed++; return; }
            EnsureView();
            VehicleRecord rec;
            bool known = Rec(s.Id, out rec);
            bool owns = known && FleetView.SenderSimulates(rec, _other, s.Region);
            var v = _filter.Check(s, known, owns, _region);
            switch (v)
            {
                case VehicleVerdict.Stale: _stale++; return;
                case VehicleVerdict.UnknownVehicle: _unknown++; return;
                case VehicleVerdict.NotDriverAuthority: _notOwner++; return;
                case VehicleVerdict.TooMany: _tooMany++; return;
            }
            if (_host)
            {
                _world.Host.Motion.Report(s.Id, (s.Flags & VehicleStateFlags.Moving) != 0, (s.Flags & VehicleStateFlags.Driven) != 0, _world.Clock);
                _lastSnap[s.Id] = new KeyValuePair<double, VehicleSnapshot>(Now, s);
                IngestRemote(s, rec);
            }
            if (v == VehicleVerdict.WrongRegion) { _wrongRegion++; return; }
            _accepted++;
            foreach (var lc in _local)
            {
                if (lc.Id != s.Id) continue;
                // the original object of this car follows (observed); if it is not observed yet, reconcile presents it
                lc.Follow.Add(s, Now); lc.FollowRecv = Now; lc.Remote.Note(s, Now); lc.SteerTarget = s.Steer;
                if (!lc.Observed) _reconcile = true;
                return;
            }
        }

        // Host: authoritative pose/fuel of a client-held car (any region; holder + region validated by the caller).
        void IngestRemote(VehicleSnapshot s, VehicleRecord rec)
        {
            if (rec.Reserved || RegionDirectory.RegionOf(_world.Registry, _other) != s.Region) return;
            ParkPolicy pp;
            if (!_remotePark.TryGetValue(s.Id, out pp)) _remotePark[s.Id] = pp = new ParkPolicy();
            var fleet = _world.Host.Fleet;
            if (fleet.SetEngine(s.Id, (s.Flags & VehicleStateFlags.EngineOn) != 0, _world.Host.Outbox)) _commits++;
            if (fleet.SetTpp(s.Id, (s.Flags & VehicleStateFlags.Tpp) != 0, _world.Host.Outbox)) _commits++;
            if (pp.ShouldCommit(Now, s.Pos, s.Rot, s.Fuel, (byte)s.Flags)
                && fleet.SetPark(s.Id, new ParkRecord { Region = s.Region, HasPose = true, Pos = s.Pos, Rot = s.Rot, Fuel = s.Fuel }, _world.Host.Outbox)) _commits++;
        }

        // Debug only (config Vehicles.InteractDebug): the same ray PlayerScript.Update casts for E (cam, forward, 2.25 m,
        // PlayerScript.mask), all hits in order with the transform the game would read (RaycastHit.transform).
        void RayProbe(UnityEngine.Object gm)
        {
            try
            {
                var ps = gm != null ? _gmPS(gm) : null;
                var cam = ps != null && _psCam != null ? _psCam(ps) as Transform : null;
                if (cam == null) return;
                int mask = _psMask != null ? _psMask(ps).value : ~0;
                var hits = Physics.RaycastAll(cam.position, cam.TransformDirection(Vector3.forward), 2.25f, mask);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                var parts = new List<string>();
                foreach (var h in hits)
                    parts.Add(h.distance.ToString("0.00") + "m " + h.collider.name + "[L" + h.collider.gameObject.layer + (h.collider.isTrigger ? " trig" : "") + "] -> " + h.transform.name + " tag=" + h.transform.tag);
                _log.Info("INTERACT_RAY " + (parts.Count == 0 ? "no hit" : string.Join(" | ", parts.ToArray())));
            }
            catch (Exception ex) { _log.Warn("INTERACT_RAY failed: " + ex.Message); }
        }

        void OnRemoteConnected(string id) { foreach (var lc in _local) lc.Policy.Reset(); _remotePark.Clear(); }

        void OnRemoteDisconnected(string reason)
        {
            _filter.Clear(); _remotePark.Clear(); _ownIds.Clear(); _changes.Clear(); _lastSnap.Clear(); _doorIds.Clear();
            if (_take != null) EndTake("disconnected", false);
            if (_ride != null) ForceExitRide("disconnected", true);
            _enterResume = null;
            if (_host && _world.Host != null) _world.Host.Motion.Clear();
            foreach (var lc in _local) { lc.Follow.Clear(); lc.FollowRecv = -100; lc.Remote.Clear(); lc.DoorRep.Pending = 0; }
            if (_host) { foreach (var lc in _local) lc.Policy.Reset(); return; }
            _viewRev = uint.MaxValue;
            if (_gate.Pending)
            {
                uint id = _gate.Abandon(); _pendingGm = null; _pendingMethod = null; _travelStatus = "disconnected";
                _log.Warn("TRAVEL request #" + id + " dropped: disconnected (the host rolls back its reservation)");
            }
        }

        // ======================================================================== safe player placement (once per arrival)
        static readonly RaycastHit[] _hits = new RaycastHit[16];
        static readonly Collider[] _cols = new Collider[32];
        readonly List<Vec3> _cand = new List<Vec3>(32);
        Type _trafficType;
        object _astar; MethodInfo _getNearest; FieldInfo _nnPos, _nnNode; PropertyInfo _walkable; bool _astarResolved;

        void TryPlace(double now, UnityEngine.Object gm, UnityEngine.Object ps, int region)
        {
            var ctx = _ctx;
            if (ctx == null || ctx.Dest != region) { _placed = true; return; }   // continue/load/scripted: original position kept
            if (now - _regionAt < 0.5) return;                                     // after ContinueButtonActivate moved the car
            bool otherHere = RegionDirectory.RegionOf(_world.Registry, _other) == region;
            Vector3 remoteFeet = Vector3.zero; bool remoteInCar = false; float remoteSpeed = 0f;
            bool remote = otherHere && _players != null && _players.TryGetRemote(out remoteFeet, out remoteInCar, out remoteSpeed);
            if (otherHere && !remote && now - _regionAt < 3.0) return;            // give the remote position a moment to arrive
            _placed = true;
            _ctx = null;
            var psc = ps as Component;
            if (psc == null || ctx.Outcome == TravelOutcome.Special) return;
            LocalCar mineCar = null;
            foreach (var lc in _local) if (lc.Mine && lc.Active && !lc.IsProxy && lc.Id == ctx.Car) mineCar = lc;
            if (ctx.Outcome == TravelOutcome.Carry && TravelRules.SeatsOnArrival(region) && _psInVehicle(ps)) { _log.Info("ARRIVAL " + Regions.Name(region) + ": seated in the car brought along (original behaviour)"); return; }
            // Parked car taken over: ContinueButtonActivate moved GameManager.Car to the leave point -> back to where it was left.
            if (ctx.Outcome == TravelOutcome.Parked && mineCar != null)
            {
                ParkRecord p;
                if (_view.TryGetPark(mineCar.Id, out p) && p.HasPose && p.Region == region)
                {
                    mineCar.T.SetPositionAndRotation(new Vector3(p.Pos.X, p.Pos.Y, p.Pos.Z), new Quaternion(p.Rot.X, p.Rot.Y, p.Rot.Z, p.Rot.W));
                    if (mineCar.Rb != null) { mineCar.Rb.velocity = Vector3.zero; mineCar.Rb.angularVelocity = Vector3.zero; }
                    _log.Info("ARRIVAL " + CarName(mineCar.Id) + " restored to its parking pose " + p);
                }
            }
            bool fixedSpawn = !TravelRules.SeatsOnArrival(region);   // hideout / Auto Shop / Verwood: original spawn point used
            var playerT = psc.transform;
            var cc = _psCC(ps) as CharacterController;
            Vector3 here = cc != null && cc.enabled ? new Vector3(playerT.position.x, cc.bounds.min.y, playerT.position.z) : playerT.position;
            LocalCar obs = null;
            if (ctx.Car != VehicleIds.None) foreach (var l in _local) if (l.Id == ctx.Car && l.Observed && l.Active) obs = l;
            Vector3? carPos = mineCar != null ? mineCar.T.position : obs != null ? obs.T.position : (Vector3?)null;
            if (carPos == null && ctx.Car != VehicleIds.None) { ParkRecord p; if (_view.TryGetPark(ctx.Car, out p) && p.HasPose && p.Region == region) carPos = new Vector3(p.Pos.X, p.Pos.Y, p.Pos.Z); }
            Vector3? leave = LeavePoint(gm, region);
            Vector3? avoid = remote ? remoteFeet : (Vector3?)null;
            Vector3? avoidCar = mineCar == null ? carPos : null;   // proxies have no colliders

            if (fixedSpawn && (avoid == null || Flat(here, avoid.Value) >= 1.2f) && (avoidCar == null || Flat(here, avoidCar.Value) >= 3.2f))
            { _log.Info("ARRIVAL " + Regions.Name(region) + " (" + ctx.Outcome + "): original spawn kept (clear of the other player and cars)"); return; }

            var anchors = new List<KeyValuePair<string, Vector3>>();
            if (remote && !remoteInCar && remoteSpeed < 3f) anchors.Add(new KeyValuePair<string, Vector3>("other player", remoteFeet));
            if (carPos != null) anchors.Add(new KeyValuePair<string, Vector3>("region car", carPos.Value));
            if (fixedSpawn) anchors.Add(new KeyValuePair<string, Vector3>("original spawn", here));
            if (leave != null) anchors.Add(new KeyValuePair<string, Vector3>("leave point", leave.Value));
            long t0 = Stopwatch.GetTimestamp(); int tested = 0;
            foreach (var a in anchors)
            {
                _cand.Clear();
                if (a.Key == "leave point" || a.Key == "original spawn") _cand.Add(new Vec3(a.Value.x, a.Value.y, a.Value.z));
                SpawnPlanner.Ring(new Vec3(a.Value.x, a.Value.y, a.Value.z), 180f, _cand);
                for (int i = 0; i < _cand.Count; i++)
                {
                    tested++;
                    Vector3 feet;
                    if (!IsSafe(new Vector3(_cand[i].X, _cand[i].Y, _cand[i].Z), a.Value, playerT, avoid, avoidCar, out feet)) continue;
                    Place(playerT, cc, feet, ps);
                    _placements++; if (ctx.Recovery) _loadPlacements++;
                    _log.Info("ARRIVAL " + Regions.Name(region) + " (" + (ctx.Recovery ? "load without travel" : ctx.Outcome.ToString()) + "): placed near " + a.Key + " at " + Fmt(feet) + " after " + tested + " candidate(s) in "
                        + ((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency).ToString("0.00") + " ms");
                    return;
                }
            }
            _placeFallbacks++;
            if (!fixedSpawn && leave != null)
            {
                Vector3 feet;
                if (GroundAt(leave.Value, playerT, out feet)) { Place(playerT, cc, feet, ps); if (ctx.Recovery) _loadPlacements++; _log.Warn("ARRIVAL " + Regions.Name(region) + ": no checked position found (" + tested + " tested); placed at the leave point " + Fmt(feet)); return; }
            }
            _log.Warn("ARRIVAL " + Regions.Name(region) + ": no safe position found (" + tested + " tested); original position kept");
        }

        static float Flat(Vector3 a, Vector3 b) { float dx = a.x - b.x, dz = a.z - b.z; return Mathf.Sqrt(dx * dx + dz * dz); }
        static string Fmt(Vector3 v) { return v.x.ToString("0.0") + "," + v.y.ToString("0.0") + "," + v.z.ToString("0.0"); }

        // ---- W4.2 Player-2 spawn (CampaignBootstrap): the arrival checks above, reused for the persistent spawn position ----
        internal bool SpawnReady { get { return _ok; } }

        // Same test as an arrival candidate: ground (normal >= 0.7, not on a car), within 4 m height of the anchor, free capsule,
        // >= 1.2 m from the other player present in this region, no traffic within 6 m.
        internal bool SpawnSpot(Vector3 candidate, Vector3 anchor, out Vector3 feet)
        {
            feet = candidate;
            var gm = _world.GameManagerObject; var ps = gm != null ? _gmPS(gm) as Component : null;
            if (ps == null) return false;
            Vector3 rf; bool rin; float rs;
            Vector3? avoid = TryGetRemoteHere(out rf, out rin, out rs) ? rf : (Vector3?)null;
            return IsSafe(candidate, anchor, ps.transform, avoid, null, out feet);
        }

        internal bool SpawnPlace(Vector3 feet)
        {
            var gm = _world.GameManagerObject; var ps = gm != null ? _gmPS(gm) : null; var psc = ps as Component;
            if (psc == null) return false;
            Place(psc.transform, _psCC(ps) as CharacterController, feet, ps);
            return true;
        }

        // The other player's feet when it is in this region and its snapshots are live.
        internal bool TryGetRemoteHere(out Vector3 feet, out bool inCar, out float speed)
        {
            feet = Vector3.zero; inCar = false; speed = 0f;
            return RegionDirectory.RegionOf(_world.Registry, _other) == _world.LocalRegion && _players != null && _players.TryGetRemote(out feet, out inCar, out speed);
        }

        // Nearest original leave point (GameManager.leavePoints, collected by ContinueButtonActivate) - Madison / Hillville only.
        internal Vector3? SpawnLeavePoint(Vector3 near)
        {
            var gm = _world.GameManagerObject;
            if (gm == null || !TravelRules.SeatsOnArrival(_world.LocalRegion)) return null;
            var pts = _gmLeave(gm);
            if (pts == null) return null;
            Vector3? best = null; float bd = float.MaxValue;
            foreach (var p in pts) { if (p == null) continue; float d = Flat(p.transform.position, near); if (d < bd) { bd = d; best = p.transform.position; } }
            return best;
        }

        // GameManager.ContinueButtonActivate: Madison/Hillville leave points, index from SPrefs MadisonPoint/HillvillePoint.
        Vector3? LeavePoint(UnityEngine.Object gm, int region)
        {
            if (!TravelRules.SeatsOnArrival(region)) return null;
            var pts = _gmLeave(gm);
            if (pts == null || pts.Length == 0) return null;
            int idx = Pref(region == 99 ? "MadisonPoint" : "HillvillePoint");
            if (idx < 0 || idx >= pts.Length || pts[idx] == null) idx = 0;
            return pts[idx] != null ? pts[idx].transform.position : (Vector3?)null;
        }

        bool GroundAt(Vector3 c, Transform player, out Vector3 feet)
        {
            feet = c;
            int n = Physics.RaycastNonAlloc(c + Vector3.up * 3f, Vector3.down, _hits, 8f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue; bool found = false;
            for (int i = 0; i < n; i++)
            {
                var h = _hits[i];
                if (h.collider == null || h.collider.transform.IsChildOf(player) || h.distance >= best) continue;
                best = h.distance; feet = h.point; found = h.normal.y >= 0.7f && !IsVehicle(h.collider);
            }
            return found;
        }

        bool IsSafe(Vector3 c, Vector3 anchor, Transform player, Vector3? avoid, Vector3? avoidCar, out Vector3 feet)
        {
            SnapToWalkable(ref c);
            if (!GroundAt(c, player, out feet)) return false;                                         // no ground / steep / on a car
            if (Mathf.Abs(feet.y - anchor.y) > 4f) return false;                                     // roofs / below the street
            if (avoid != null && Flat(feet, avoid.Value) < 1.2f) return false;                        // other player (no collider)
            if (avoidCar != null && Flat(feet, avoidCar.Value) < 3.2f) return false;                  // remote car proxy (no collider)
            const float r = 0.35f;
            int n = Physics.OverlapCapsuleNonAlloc(feet + Vector3.up * (r + 0.1f), feet + Vector3.up * (1.8f - r), r, _cols, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++) if (_cols[i] != null && !_cols[i].transform.IsChildOf(player)) return false;   // walls, buildings, cars
            n = Physics.OverlapSphereNonAlloc(feet, 6f, _cols, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++) if (_cols[i] != null && IsTraffic(_cols[i])) return false;                     // traffic nearby
            return true;
        }

        bool IsVehicle(Collider c)
        {
            var rb = c.attachedRigidbody;
            return rb != null && ((!rb.isKinematic && rb.mass > 200f) || _observedBodies.Contains(rb));   // never place on/in an observed car
        }

        bool IsTraffic(Collider c)
        {
            var rb = c.attachedRigidbody;
            return _trafficType != null && rb != null && rb.GetComponent(_trafficType) != null;
        }

        // Type/member lookup at startup (ResolveApi); the AstarPath instance itself is per scene (static 'active').
        FieldInfo _astarActive;
        void ResolveAstar()
        {
            _astarResolved = true;
            var t = AccessTools.TypeByName("AstarPath");
            var nn = AccessTools.TypeByName("Pathfinding.NNInfo");
            var node = AccessTools.TypeByName("Pathfinding.GraphNode");
            _astarActive = t != null ? AccessTools.Field(t, "active") : null;
            _getNearest = t != null ? AccessTools.Method(t, "GetNearest", new[] { typeof(Vector3) }) : null;
            _nnPos = nn != null ? AccessTools.Field(nn, "position") : null;
            _nnNode = nn != null ? AccessTools.Field(nn, "node") : null;
            _walkable = node != null ? AccessTools.Property(node, "Walkable") : null;
        }

        // The game's own NPC navigation graph (A* Pathfinding Project): prefer a walkable node near the candidate.
        void SnapToWalkable(ref Vector3 c)
        {
            try
            {
                if (!_astarResolved) ResolveAstar();
                if (_astar == null || (_astar as UnityEngine.Object) == null) _astar = _astarActive != null ? _astarActive.GetValue(null) : null;
                if (_astar == null || (_astar as UnityEngine.Object) == null || _getNearest == null || _nnPos == null || _nnNode == null || _walkable == null) return;
                var info = _getNearest.Invoke(_astar, new object[] { c });
                var n = _nnNode.GetValue(info);
                if (n == null || !(bool)_walkable.GetValue(n, null)) return;
                var p = (Vector3)_nnPos.GetValue(info);
                if (Flat(p, c) < 1.5f && Mathf.Abs(p.y - c.y) < 3f) c = p;
            }
            catch { _astar = null; }
        }

        // Teleport + reset the original fall speed (PlayerMotorBehavior.velocityY) so a player recovered from a long fall
        // does not keep its downward velocity into the first CharacterController.Move.
        void Place(Transform player, CharacterController cc, Vector3 feet, UnityEngine.Object ps)
        {
            Teleport(player, cc, feet);
            var pmb = ps != null ? _psPMB(ps) : null;
            if (pmb != null && _pmbSetVelY != null) _pmbSetVelY(pmb, 0f);
            _airSince = -1;
        }

        // M5 hotfix: a player that keeps falling far below its last grounded position (or below FallRules.AbsoluteFloor
        // without one) is returned to verified ground: the last grounded position in this region, else the region's original
        // leave point, else the other player. At most FallRules.MaxPerRegion recoveries per region visit, FallRules.MinInterval
        // apart; then it gives up (logged) - never a teleport loop, never a valid player (grounded / small jumps / in a car).
        void FallGuard(double now, UnityEngine.Object gm, UnityEngine.Object ps, int region)
        {
            if (now < _nextFallCheck) return;
            _nextFallCheck = now + 0.5;
            var psc = ps as Component; var cc = _psCC(ps) as CharacterController;
            if (psc == null || cc == null || !cc.enabled || _psInVehicle(ps)) { _airSince = -1; return; }
            var t = psc.transform;
            var feet = new Vector3(t.position.x, cc.bounds.min.y, t.position.z);
            if (cc.isGrounded) { _safeFeet = feet; _safeRegion = region; _airSince = -1; return; }
            if (_airSince < 0) { _airSince = now; return; }
            bool hasSafe = _safeRegion == region;
            if (!FallRules.IsLost(now - _airSince, feet.y, hasSafe, _safeFeet.y)) return;
            if (_fallGaveUp) return;
            if (!FallRules.MayRecover(_fallRecoveriesHere, now - _lastFallRecovery))
            {
                if (_fallRecoveriesHere >= FallRules.MaxPerRegion) { _fallGaveUp = true; _log.Error("FALL_RECOVERY gave up in " + Regions.Name(region) + " after " + _fallRecoveriesHere + " recoveries (player at " + Fmt(feet) + "): reload the region (travel) to recover"); }
                return;
            }
            Vector3 dest; string from = null;
            if (hasSafe && GroundAt(_safeFeet, t, out dest)) from = "last grounded position";
            else
            {
                dest = Vector3.zero;
                Vector3? leave = LeavePoint(gm, region);
                Vector3 rf = Vector3.zero; bool rc = false; float rs = 0f;
                bool remote = RegionDirectory.RegionOf(_world.Registry, _other) == region && _players != null && _players.TryGetRemote(out rf, out rc, out rs) && !rc;
                if (leave != null && GroundAt(leave.Value, t, out dest)) from = "original leave point";
                else if (remote && GroundAt(rf, t, out dest)) from = "other player";
            }
            _lastFallRecovery = now; _fallRecoveriesHere++;
            if (from == null) { _log.Warn("FALL_RECOVERY " + Regions.Name(region) + ": player lost at " + Fmt(feet) + " (" + (now - _airSince).ToString("0.0") + " s airborne) but no verified ground found (attempt " + _fallRecoveriesHere + ")"); return; }
            float airborne = (float)(now - _airSince);
            Place(t, cc, dest, ps);
            _fallRecoveries++;
            _log.Warn("FALL_RECOVERY " + Regions.Name(region) + ": player was falling at " + Fmt(feet) + " for " + airborne.ToString("0.0") + " s -> placed at the " + from + " " + Fmt(dest)
                + " (recovery " + _fallRecoveriesHere + "/" + FallRules.MaxPerRegion + " in this region)");
        }

        static void Teleport(Transform player, CharacterController cc, Vector3 feet)
        {
            bool en = cc != null && cc.enabled;
            float offset = en ? Mathf.Clamp(player.position.y - cc.bounds.min.y, 0f, 2.5f) : 0f;
            if (en) cc.enabled = false;
            player.position = feet + Vector3.up * (offset + 0.05f);
            if (en) cc.enabled = true;
        }

        // ---- hideout / Auto Shop / Verwood without a car of one's own: open the original travel menu on foot ----
        void TravelMenuKey(UnityEngine.Object ps, int region)
        {
            if (ps == null || TravelRules.SeatsOnArrival(region)) return;   // Madison/Hillville: the map borders open the menu on foot
            if (_psInVehicle(ps) || _psChase(ps)) return;
            foreach (var lc in _local) if (lc.Mine && lc.Active && !lc.IsProxy) return;   // own car here: original way (engine start)
            var cur = _psVehicle(ps) as Component;
            if (cur != null && !_isPlayerCar(cur)) return;   // last vehicle a stolen car: OpenTownSelectionNoDelay would run the delivery path
            _log.Info("TRAVEL menu opened with " + _cfg.TravelMenuKey + " (no car of this player in " + Regions.Name(region) + ")");
            _openTravel(ps);
        }

        // M4.3.2a permanent rule (AGENTS.md / CLAUDE.md): the mod never puts its own text into the gameplay UI. Routine
        // multiplayer feedback (refused seat, car moving, host not answering, ...) goes to the BepInEx log only, de-duplicated
        // for 2 s; the game itself shows nothing for an interaction that does nothing.
        string _lastQuiet; double _lastQuietAt = -100;
        void Quiet(string s)
        {
            double now = Now;
            if (s == _lastQuiet && now - _lastQuietAt < 2.0) return;
            _lastQuiet = s; _lastQuietAt = now; _quiet++;
            _log.Info("FEEDBACK (log only, not shown in game) " + s);
        }

        // ======================================================================== diagnostics
        void BuildStatus()
        {
            var parts = new List<string>();
            foreach (var kv in _view.All)
                parts.Add(CarName(kv.Key) + "@" + Regions.Name(kv.Value.Region) + (kv.Value.Reserved ? "*" : "") + (kv.Value.Holder == _me ? "(here)" : kv.Value.Holder != 0 ? "(P" + kv.Value.Holder + ")" : "(parked)"));
            bool noCar = TravelRules.IsFleetRegion(_region) && !TravelRules.SeatsOnArrival(_region) && !_local.Any(l => l.Mine && l.Active && !l.IsProxy);
            _status = "cars " + (parts.Count > 0 ? string.Join(" ", parts.ToArray()) : "-") + " | travel " + (_gate.Pending ? "waiting for host" : _travelStatus)
                + (noCar ? " | " + _cfg.TravelMenuKey + ": travel menu" : "");
        }

        public string StatusLine { get { return _status; } }

        void LogStats()
        {
            double avgUs = _frames > 0 ? _costTicks * 1e6 / Stopwatch.Frequency / _frames : 0;
            double maxUs = _costMax * 1e6 / Stopwatch.Frequency;
            _log.Info("VEHICLE_STATS local=" + _local.Count + " held=" + _local.Count(l => l.Mine) + " observed=" + _observedBodies.Count + " sent=" + _sent + "/" + _sentBytes + "B recv=" + _recv + "/" + _recvBytes + "B accepted=" + _accepted
                + " stale=" + _stale + " unknown=" + _unknown + " notOwner=" + _notOwner + " wrongRegion=" + _wrongRegion + " tooMany=" + _tooMany + " malformed=" + _malformed
                + " observeEnter=" + _observeEnter + " pushOuts=" + _pushOuts + " doors rep/applied/refused=" + _doorReports + "/" + _doorApplies + "/" + _doorRefused + " commits=" + _commits + " act/deact=" + _activations + "/" + _deactivations
                + " | travel clicks=" + _travels + " held=" + _held + " resumed=" + _resumed + " denied=" + _denied + " cancelled=" + _cancelled + " phoneBlocked=" + _phoneBlocked
                + " proxyMenus=" + _proxyMenus + " placed=" + _placements + " placeFallbacks=" + _placeFallbacks + " loadPlacements=" + _loadPlacements + " fallRecoveries=" + _fallRecoveries
                + " | passenger req/granted/denied/seated/exits/forced=" + _rideRequests + "/" + _rideGranted + "/" + _rideDenied + "/" + _rideSeated + "/" + _rideExits + "/" + _rideForced + " camSwitches=" + _camSwitches
                + " footEntries=" + _footEntries + " storage open/refused=" + _storageOpens + "/" + _storageRefused + " medkitRefused=" + _medkitRefused + " selfBodyShown=" + _selfShown
                + " slowDoors rep/applied/fastFinish=" + _slowReports + "/" + _slowApplies + "/" + _fastFinishes
                + " entryPromptShown=" + _hintShows + " exitBlocked=" + _exitBlocked + " canInteractKept=" + _canInteractKept + " logOnlyFeedback=" + _quiet
                + " seat+foot avg=" + (_frames > 0 ? (_rideFootTicks * 1e6 / Stopwatch.Frequency / _frames).ToString("0.0") : "0") + "us"
                + " | handoff req/granted/denied/entered=" + _takeRequests + "/" + _takeGranted + "/" + _takeDenied + "/" + _takeEntered + " solidSwitches=" + _solidSwitches + " seatFrames=" + _seatPosed
                + (_host ? " fleetWrites=" + _fleetWrites + "/" + (_writer != null ? _writer.Writes + " fail=" + _writer.Failures : "-") + " reservations=" + _world.Host.Travel.Pending
                    + " decisions/commits/rollbacks=" + _world.Host.Travel.Decisions + "/" + _world.Host.Travel.Commits + "/" + _world.Host.Travel.Rollbacks : "")
                + " | joint holds/departures/departFailed/seated/onFoot/recovered/menuBlocked=" + _jHolds + "/" + _jDepartures + "/" + _jDepartFailed + "/" + _jSeated + "/" + _jOnFoot + "/" + _jRecovered + "/" + _jTownBlocked
                + " loadMax=" + _jLoadMax.ToString("0.0") + "s readyMax=" + _jReadyMax.ToString("0.0") + "s"
                + (_host ? " trips started/committed/cancelled/failed/refused=" + _world.Host.Joint.Started + "/" + _world.Host.Joint.Committed + "/" + _world.Host.Joint.Cancelled + "/" + _world.Host.Joint.Failed + "/" + _world.Host.Joint.Refused
                    + " lastTrip=" + _world.Host.Joint.LastDuration.ToString("0.0") + "s" : "")
                + " | update avg=" + avgUs.ToString("0.0") + "us max=" + maxUs.ToString("0") + "us frames=" + _frames);
            _costTicks = 0; _costMax = 0; _frames = 0; _rideFootTicks = 0;
        }

        public void Dispose()
        {
            _node.MessageReceived -= OnMessage;
            _node.RemoteDisconnected -= OnRemoteDisconnected;
            _node.RemoteConnected -= OnRemoteConnected;
            if (ReferenceEquals(TravelHooks.Handler, this)) TravelHooks.Uninstall();
            _world.ProposalResultHook = null;
            if (_host && _world.Host != null) _world.Host.LivePark = null;
            if (_host && _fleetReady) { try { _fleetDirty = true; FlushFleet(true); _log.Info("FLEET final write " + (_store != null ? _store.Path : "-")); } catch (Exception ex) { _log.Error("FLEET final write failed", ex); } }
            if (_ride != null) { try { ForceExitRide("plugin shutdown", true); } catch { } }
            if (_selfBody != null) { _selfBody.Destroy(); _selfBody = null; }
            foreach (var lc in _local) { if (lc.Observed) ExitObserved(lc); else if (lc.IsProxy) ExitProxy(lc); }
            _observedBodies.Clear();
        }
    }
}
