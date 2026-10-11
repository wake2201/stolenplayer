# Observed live player hierarchy

Historical hierarchy excerpt. R7 later captured all 1,186 player nodes with explicit
complete status; R9 verified genuine recreation at the same path with PS ID
316802→953900. Current hands/bones/cameras/animation and crouch findings are in
[player-animation-inventory.md](player-animation-inventory.md); evidence keys in
[full-runtime-recon.md](full-runtime-recon.md). The 160-node limit below applies only
to the earlier capture.

Evidence: ignored `recon-20261006-112054-5e8ec02f.ndjson`, eight gameplay captures
across two MainScene lifetimes. Paths include sibling indices. This records
observed hierarchy facts, not a prefab reconstruction. The original 160-node
cap leaves deep animation bones/hand equipment descendants incomplete.

Player root: `IMPORTANT OBJECTS[0]/First-Person Character[19]`, scene MainScene.
The scene-root ancestor is IMPORTANT OBJECTS, rather than a persistent prefab.

Root components: Transform, CharacterController, PlayerMotorBehavior,
PlayerCameraBehavior, PlayerStaminaBehavior, PlayerCursorManager, AudioSource,
PlayerScript, ShadowDetector, CapsuleCollider, Rigidbody. PlayerScript.CC,
PMB and PCB references resolve to this same GameObject.

## All immediate children (observed)

Transform is implicit in the table. Names and sibling indices are exact.

| Child | Additional components / role |
|---|---|
| Inside Building Sensor[0] | SensorToolkit.RaySensor |
| Ceiling Sensor[1] | RaySensor |
| DropItem[2] | Transform only; item drop reference |
| Tilt[3] | HeadTilt; head/camera/hands ancestor |
| ReverbInside[4] | AudioReverbZone |
| SequenceVoice[5] | AudioSource |
| CallVibrate[6] | AudioSource |
| PhoneInterference[7] | AudioSource |
| PickUpCall[8] | AudioSource |
| HangUp[9] | AudioSource |
| EndCall[10] | AudioSource |
| CrouchDownSound[11] | AudioSource |
| CrouchUpSound[12] | AudioSource |
| Night Vision[13] | Night_Vision_Script, AudioSource |
| Car_Trigger[14] | Inactive; Rigidbody, CapsuleCollider |
| Trigger Map[15] | BoxCollider |
| exhausted[16] | AudioSource |
| jump sound[17] | AudioSource |
| RainTriggerInterior[18] | BoxCollider |
| Rain Trigger Car[19] | BoxCollider |
| Possesion Sensor[20] | SphereCollider, Rigidbody |
| PC Tutorial Voice[21] | AudioSource |
| fill gas sound[22] | AudioSource |
| Hand Light[23] | Light |
| Crouch Sensor[24] | RaySensor |
| Cough[25] | AudioSource |
| spawn behind pos[26] | Transform only |
| JumpTrigger[27] | BoxCollider |
| jump obstacle[28] | AudioSource |
| RelockDoor[29] | AudioSource |
| Whistle[30] | AudioSource |
| Reflection Probe Player[31] | ReflectionProbe, ReflectionProbeUpdate |
| level up sound[32] | AudioSource |
| Got Shot Camera[33] | Inactive; Camera, AudioSource and blood/grayscale/vignette filters |
| drone disable sound[34] | AudioSource |
| Floor Sensor[35] | RaySensor |
| LEGS[36] | LegsController |
| drunk source[37] | AudioSource |
| Reverb Drunk[38] | Inactive; AudioReverbZone |
| door break creak[39] | AudioSource |
| break door[40] | AudioSource |
| break baton[41] | AudioSource |
| award[42] | AudioSource |
| CameraCollider[43] | CapsuleCollider |
| Big Crash[44] | AudioSource |
| EAT SOUND[45] | AudioSource |
| DRINK SOUND[46] | AudioSource |

## Relevant expanded branches

```text
First-Person Character[19]
  Tilt[3] (HeadTilt)
    Head[0] (Transform; PCB.headTransform)
      CamAnim[0] (legacy Animation)
        SwitchSound[0] (AudioSource)
        Camera[1] (PCB.playerCamera)
          Camera, FlareLayer, AudioListener, CarSmoothFollow
          SSAOPro, PostProcessLayer, Beautify, EnviroSkyRendering,
          EnviroPostProcessing, TranslucentImageSource,
          WetStuff.EditorRestartHack and camera distortion/drawing filters
          normal pos heavy[0], high pos heavy[1] (Transforms)
          Hold Item Positions[2] (Transform; indexed placement children)
          Attack Sensor[3] (RaySensor)
          Outline Camera[4] (Camera)
          Flashlight[5] (inactive; Light, Delay, VolumetricLightBeamHD/CookieHD)
          Crowbar Impact[6], Hand Foley[7], Money Pick[8] (AudioSources)
          Camera_SSAA[9] (Camera)
          Exclamation Markers Camera[10] (Camera, Match_FoV, Beautify)
          Throw Brick[11] (AudioSource)
          Tranq Sensor[12], Binoculars Interior Sensor[13],
          Binoculars Item Sensor[14] (RaySensors)
      HandsCam[1] (Camera, legacy Animation, Light, PostProcessLayer,
                   Beautify, night-vision and other camera filters)
        Crossbow[0] (inactive; armature/weapon skinned renderers)
        Respirator[1] (MeshFilter/MeshRenderer, Animation; renderer disabled)
        PICIE ALKOHOLU[2] (inactive)
          PRAWA REKA[0] (Animation branch; audio and armMesh renderer)
        HANDS[3]
          Nowe Łapy[0]
            Changing Item L[0] (Animation)
              LEWA REKA[0] (Animation)
            Changing Item R[1] (Animation)
              Sound1[0] (AudioSource)
              PRAWA REKA[1] (Animation)
            PODNOSZENIE[2] (inactive; Animation)
            ZAKLADANIE MASKI[3] (inactive)
        CROWBAR BREAKIN[4] (inactive; PRAWA REKA animation/audio/armMesh)
        USING SHOVEL[5] (inactive; PRAWA REKA animation/audio/armMesh)
  LEGS[36] (LegsController)
    Main Character Legs[0] (Animator, FinalIK.GrounderIK)
      Body[0] (inactive; SkinnedMeshRenderer, mesh Body)
      Bottoms[1] (active; SkinnedMeshRenderer, mesh Bottoms)
      Eyelashes[2] (inactive; SkinnedMeshRenderer, mesh Eyelashes)
      Eyes[3] (inactive; SkinnedMeshRenderer, mesh Eyes)
      Gloves[4] (inactive; SkinnedMeshRenderer, mesh Gloves)
      Hats[5] (inactive; SkinnedMeshRenderer, mesh Hats)
      mixamorig:Hips[6]
        mixamorig:LeftUpLeg[0] (FinalIK.LimbIK)
          mixamorig:LeftLeg[0]/mixamorig:LeftFoot[0]
        mixamorig:RightUpLeg[1] (FinalIK.LimbIK)
          mixamorig:RightLeg[0]/mixamorig:RightFoot[0]
        mixamorig:Spine[2]/mixamorig:Spine1[0]/mixamorig:Spine2[0]
      Shoes[7] (active; SkinnedMeshRenderer, mesh Shoes)
      Tops[8] (inactive; SkinnedMeshRenderer, mesh Tops)
```

The primary Camera and HandsCam are separate from the movement root. Root/CC
position is authoritative; camera world position includes lean/headbob and
pitch. Pitch is camera-local X, body yaw is root Euler Y written by PCB.Update.
The old capture did not include numeric camera positions/yaw or culling masks;
those are added to the upgraded diagnostics for follow-up verification.

## Alternative model and unsafe components

Vehicle candidates were instantiated but inactive:
`MUSTANG[0]/Essentials[2]/TPP Character[11]` and
`PLAYER CAR[0]/Essentials[2]/TPP Character[12]`.
They have Animation and CheckItemEnable plus skinned body parts. CheckItemEnable
resolves the local Player tag and changes equipment activation based on inventory.
Do not instantiate those character roots. Hidden local body geometry is a
closer, smaller asset-only source; vehicle/NPC models are unnecessary.

- PlayerScript binds Rewired player 0, owns UI/inventory/save/progression and
  interactions, and modifies local collision/input/hand initialization.
- PlayerMotorBehavior and PlayerCameraBehavior bind Rewired player 0 and move
  the genuine root/camera. HeadTilt also reads player 0 for leaning.
- LegsController reads PlayerScript's Rewired movement, vehicle/drone/hiding
  state and mutates the local Animator and arm tilt. It is not a remote animator.
- PlayerCursorManager changes the global Cursor lock/visibility in Start/Update.
- CharacterController, all colliders/Rigidbodies and RaySensors may enter local
  physics/trigger/interaction systems. They are excluded even when visually inert.
- AudioListener, AudioSources/ReverbZones, cameras, lights, probes, postprocess
  and camera-follow helpers are excluded; they can affect local output or globals.
- Stamina, ShadowDetector, night vision, material/equipment helpers, IK and
  remaining game/third-party scripts have no Phase 1 role. Their complete side
  effects are not all audited, so the proxy uses an allowlist of only Transform,
  MeshFilter and MeshRenderer. No singleton/global-player registration is copied.

This avoids needing to certify every script in the 160-node captured subset as
harmless. No game script is copied, added to, disabled on or patched for the
genuine player by the multiplayer plugin.
