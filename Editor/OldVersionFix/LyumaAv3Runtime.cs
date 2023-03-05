/* Copyright (c) 2020-2022 Lyuma <xn.lyuma@gmail.com>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE. */
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using Object = System.Object;

// [RequireComponent(typeof(Animator))]
public class LyumaAv3Runtime : MonoBehaviour
{
    static public Dictionary<VRCAvatarDescriptor.AnimLayerType, RuntimeAnimatorController> animLayerToDefaultController = new Dictionary<VRCAvatarDescriptor.AnimLayerType, RuntimeAnimatorController>();
    static public Dictionary<VRCAvatarDescriptor.AnimLayerType, AvatarMask> animLayerToDefaultAvaMask = new Dictionary<VRCAvatarDescriptor.AnimLayerType, AvatarMask>();
    public delegate void UpdateSelectionFunc(UnityEngine.Object obj);
    public static UpdateSelectionFunc updateSelectionDelegate;
    public delegate void AddRuntime(Component runtime);
    public static AddRuntime addRuntimeDelegate;
    public delegate void UpdateSceneLayersFunc(int layers);
    public static UpdateSceneLayersFunc updateSceneLayersDelegate;
    public delegate void ApplyOnEnableWorkaroundDelegateType();
    public static ApplyOnEnableWorkaroundDelegateType ApplyOnEnableWorkaroundDelegate;

    // This is injected by Editor-scope scripts to give us access to VRCBuildPipelineCallbacks.
    public static Action<GameObject> InvokeOnPreProcessAvatar = (_) => { };

    public LyumaAv3Runtime OriginalSourceClone = null;

    [Tooltip("Resets avatar state machine instantly")]
    public bool ResetAvatar;
    [Tooltip("Resets avatar state machine and waits until you uncheck this to start")]
    public bool ResetAndHold;
    [Tooltip("Click if you modified your menu or parameter list")]
    public bool RefreshExpressionParams;
    [Tooltip("Simulates saving and reloading the avatar")]
    public bool KeepSavedParametersOnReset = true;
    [HideInInspector] public bool legacyMenuGUI = true;
    private bool lastLegacyMenuGUI = true;
    [Header("Animator to Debug. Unity is glitchy when not 'Base'.")]
    [Tooltip("Selects the playable layer to be visible with parameters in the Animator. If you view any other playable in the Animator window, parameters will say 0 and will not update.")]
    public VRCAvatarDescriptor.AnimLayerType DebugDuplicateAnimator;
    private char PrevAnimatorToDebug;
    [Tooltip("Selects the playable layer to be visible in Unity's Animator window. Does not reset avatar. Unless this is set to Base, will cause 'Invalid Layer Index' logspam; layers will show wrong weight and parameters will all be 0.")]
    public VRCAvatarDescriptor.AnimLayerType ViewAnimatorOnlyNoParams;
    private char PrevAnimatorToViewLiteParamsShow0;
    [HideInInspector] public string SourceObjectPath;
    [HideInInspector] public LyumaAv3Runtime AvatarSyncSource;
    private float nextUpdateTime = 0.0f;
    [Header("OSC (double click OSC Controller for debug and port settings)")]
    public bool EnableAvatarOSC = false;
    public bool LogOSCWarnings = false;


    [Header("Network Clones and Sync")]
    public bool CreateNonLocalClone;
    [Tooltip("In VRChat, 8-bit float quantization only happens remotely. Check this to test your robustness to quantization locally, too. (example: 0.5 -> 0.503")]
    public bool locally8bitQuantizedFloats = false;
    private int CloneCount;
    [Range(0.0f, 2.0f)] public float NonLocalSyncInterval = 0.2f;
    [Tooltip("Parameters visible in the radial menu will IK sync")] public bool IKSyncRadialMenu = true;
    [Header("PlayerLocal and MirrorReflection")]
    public bool EnableHeadScaling;
    public bool DisableMirrorAndShadowClones;
    [HideInInspector] public LyumaAv3Runtime MirrorClone;
    [HideInInspector] public LyumaAv3Runtime ShadowClone;
    [Tooltip("To view both copies at once")] public bool DebugOffsetMirrorClone = false;
    public bool ViewMirrorReflection;
    private bool LastViewMirrorReflection;
    public bool ViewBothRealAndMirror;
    private bool LastViewBothRealAndMirror;
    [HideInInspector] public VRCAvatarDescriptor avadesc;
    Avatar animatorAvatar;
    Animator animator;
    public Object emulator;
    private RuntimeAnimatorController origAnimatorController;
    public Dictionary<VRCAvatarDescriptor.AnimLayerType, RuntimeAnimatorController> allControllers = new Dictionary<VRCAvatarDescriptor.AnimLayerType, RuntimeAnimatorController>();

    private Transform[] allTransforms;
    private Transform[] allMirrorTransforms;
    private Transform[] allShadowTransforms;
    private List<AnimatorControllerPlayable> playables = new List<AnimatorControllerPlayable>();
    private List<Dictionary<string, int>> playableParamterIds = new List<Dictionary<string, int>>();
    private List<Dictionary<int, float>> playableParamterFloats = new List<Dictionary<int, float>>();
    private List<Dictionary<int, int>> playableParamterInts = new List<Dictionary<int, int>>();
    private List<Dictionary<int, bool>> playableParamterBools = new List<Dictionary<int, bool>>();
    AnimationLayerMixerPlayable playableMixer;
    PlayableGraph playableGraph;
    VRCExpressionsMenu expressionsMenu;
    VRCExpressionParameters stageParameters;
    int sittingIndex, tposeIndex, ikposeIndex;
    int fxIndex, altFXIndex;
    int actionIndex, altActionIndex;
    int additiveIndex, altAdditiveIndex;
    int gestureIndex, altGestureIndex;

    private int mouthOpenBlendShapeIdx;
    private int[] visemeBlendShapeIdxs;

    public Dictionary<String, object> DataLastShovedIntoOSCAnyway = new Dictionary<String, object>();
    public Dictionary<String, object> DataToShoveIntoOSCAnyway = new Dictionary<String, object>();

    [NonSerialized] public VRCPhysBone[] AvDynamicsPhysBones = new VRCPhysBone[]{};
    [NonSerialized] public VRCContactReceiver[] AvDynamicsContactReceivers = new VRCContactReceiver[]{};

    public class Av3EmuParameterAccess : VRC.SDKBase.IAnimParameterAccess {
        public LyumaAv3Runtime runtime;
        public string paramName;
        public bool boolVal {
            get {
                // Debug.Log(paramName + " GETb");
                int idx;
                if (runtime.IntToIndex.TryGetValue(paramName, out idx)) return runtime.Ints[idx].value != 0;
                if (runtime.FloatToIndex.TryGetValue(paramName, out idx))return runtime.Floats[idx].exportedValue != 0.0f;
                if (runtime.BoolToIndex.TryGetValue(paramName, out idx)) return runtime.Bools[idx].value;
                return false;
            }
            set {
                object oldObj = null;
		runtime.DataLastShovedIntoOSCAnyway.TryGetValue(paramName, out oldObj);
                if ((object)value != oldObj) {
                    runtime.DataToShoveIntoOSCAnyway[paramName] = value;
                    runtime.DataLastShovedIntoOSCAnyway[paramName] = value;
                }
                // Debug.Log(paramName + " SETb " + value);
                int idx;
                if (runtime.IntToIndex.TryGetValue(paramName, out idx)) runtime.Ints[idx].value = value ? 1 : 0;
                if (runtime.FloatToIndex.TryGetValue(paramName, out idx)) {
                    runtime.Floats[idx].value = value ? 1.0f : 0.0f;
                    runtime.Floats[idx].exportedValue = runtime.Floats[idx].value;
                }
                if (runtime.BoolToIndex.TryGetValue(paramName, out idx)) runtime.Bools[idx].value = value;
            }
        }
        public int intVal {
            get {
                int idx;
                // Debug.Log(paramName + " GETi");
                if (runtime.IntToIndex.TryGetValue(paramName, out idx)) return runtime.Ints[idx].value;
                if (runtime.FloatToIndex.TryGetValue(paramName, out idx)) return (int)runtime.Floats[idx].exportedValue;
                if (runtime.BoolToIndex.TryGetValue(paramName, out idx)) return runtime.Bools[idx].value ? 1 : 0;
                return 0;
            }
            set {
                object oldObj = null;
		runtime.DataLastShovedIntoOSCAnyway.TryGetValue(paramName, out oldObj);
                if ((object)value != oldObj) {
                    runtime.DataToShoveIntoOSCAnyway[paramName] = value;
                    runtime.DataLastShovedIntoOSCAnyway[paramName] = value;
                }
                // Debug.Log(paramName + " SETi " + value);
                int idx;
                if (runtime.IntToIndex.TryGetValue(paramName, out idx)) runtime.Ints[idx].value = value;
                if (runtime.FloatToIndex.TryGetValue(paramName, out idx)) {
                    runtime.Floats[idx].value = (float)value;
                    runtime.Floats[idx].exportedValue = runtime.Floats[idx].value;
                }
                if (runtime.BoolToIndex.TryGetValue(paramName, out idx)) runtime.Bools[idx].value = value != 0;
            }
        }
        public float floatVal {
            get {
                // Debug.Log(paramName + " GETf");
                int idx;
                if (runtime.IntToIndex.TryGetValue(paramName, out idx)) return (float)runtime.Ints[idx].value;
                if (runtime.FloatToIndex.TryGetValue(paramName, out idx)) return runtime.Floats[idx].exportedValue;
                if (runtime.BoolToIndex.TryGetValue(paramName, out idx)) return runtime.Bools[idx].value ? 1.0f : 0.0f;
                return 0.0f;
            }
            set {
                object oldObj = null;
		runtime.DataLastShovedIntoOSCAnyway.TryGetValue(paramName, out oldObj);
                if ((object)value != oldObj) {
                    runtime.DataToShoveIntoOSCAnyway[paramName] = value;
                    runtime.DataLastShovedIntoOSCAnyway[paramName] = value;
                }
                // Debug.Log(paramName + " SETf " + value);
                int idx;
                if (runtime.IntToIndex.TryGetValue(paramName, out idx)) runtime.Ints[idx].value = (int)value;
                if (runtime.FloatToIndex.TryGetValue(paramName, out idx)) {
                    runtime.Floats[idx].value = value;
                    runtime.Floats[idx].exportedValue = value;
                }
                if (runtime.BoolToIndex.TryGetValue(paramName, out idx)) runtime.Bools[idx].value = value != 0.0f;
            }
        }
    }

    public void SuppressWarnings()
    {
        object ignored = nextUpdateTime;
        object ignored2 = lastLegacyMenuGUI;
    }
    
    public void assignContactParameters(VRCContactReceiver[] behaviours) {
        AvDynamicsContactReceivers = behaviours;
        foreach (var mb in AvDynamicsContactReceivers) {
            var old_value = mb.paramAccess;
            if (old_value == null || old_value.GetType() != typeof(Av3EmuParameterAccess)) {
                string parameter = mb.parameter;
                Av3EmuParameterAccess accessInst = new Av3EmuParameterAccess();
                accessInst.runtime = this;
                accessInst.paramName = parameter;
                mb.paramAccess = accessInst;
                accessInst.floatVal = mb.paramValue;
                // Debug.Log("Assigned access " + contactReceiverState.paramAccess.GetValue(mb) + " to param " + parameter + ": was " + old_value);
            }
        }
    }
    public void assignPhysBoneParameters(VRCPhysBone[] behaviours) {
        AvDynamicsPhysBones = behaviours;
        foreach (var mb in AvDynamicsPhysBones) {
            var old_value = mb.param_Stretch;
            if (old_value == null || old_value.GetType() != typeof(Av3EmuParameterAccess)) {
                string parameter = mb.parameter;
                Av3EmuParameterAccess accessInst = new Av3EmuParameterAccess();
                accessInst.runtime = this;
                accessInst.paramName = parameter + VRCPhysBone.PARAM_ANGLE;
                mb.param_Angle = accessInst;
                accessInst.floatVal = mb.param_AngleValue;
                accessInst = new Av3EmuParameterAccess();
                accessInst.runtime = this;
                accessInst.paramName = parameter + VRCPhysBone.PARAM_ISGRABBED;
                mb.param_IsGrabbed = accessInst;
                accessInst.boolVal = mb.param_IsGrabbedValue;
                accessInst = new Av3EmuParameterAccess();
                accessInst.runtime = this;
                accessInst.paramName = parameter + VRCPhysBone.PARAM_STRETCH;
                mb.param_Stretch = accessInst;
                accessInst.floatVal = mb.param_StretchValue;
                // Debug.Log("Assigned strech access " + physBoneState.param_Stretch.GetValue(mb) + " to param " + parameter + ": was " + old_value);
            }
        }
    }

    public static float ClampFloatOnly(float val) {
        if (val < -1.0f) {
            val = -1.0f;
        }
        if (val > 1.0f) {
            val = 1.0f;
        }
        return val;
    }
    public static float ClampAndQuantizeFloat(float val) {
        val = ClampFloatOnly(val);
        val *= 127.00f;
        // if (val > 127.0f) {
        //     val = 127.0f;
        // }
        val = Mathf.Round(val);
        val = (((sbyte)val) / 127.0f);
        val = ClampFloatOnly(val);
        return val;
    }
    public static int ClampByte(int val) {
        if (val < 0) {
            val = 0;
        }
        if (val > 255) {
            val = 255;
        }
        return val;
    }

    public enum VisemeIndex {
        sil, PP, FF, TH, DD, kk, CH, SS, nn, RR, aa, E, I, O, U
    }
    public enum GestureIndex {
        Neutral, Fist, HandOpen, Fingerpoint, Victory, RockNRoll, HandGun, ThumbsUp
    }
    public enum TrackingTypeIndex {
        Uninitialized, GenericRig, NoFingers, HeadHands, HeadHandsHip, HeadHandsHipFeet = 6
    }
    public static HashSet<string> BUILTIN_PARAMETERS = new HashSet<string> {
        "Viseme", "Voice", "GestureLeft", "GestureLeftWeight", "GestureRight", "GestureRightWeight", "VelocityX", "VelocityY", "VelocityZ", "Upright", "AngularY", "Grounded", "Seated", "AFK", "TrackingType", "VRMode", "MuteSelf", "InStation"
    };
    public static readonly HashSet<Type> MirrorCloneComponentBlacklist = new HashSet<Type> {
        typeof(Camera), typeof(FlareLayer), typeof(AudioSource), typeof(Rigidbody), typeof(Joint)
    };
    public static readonly HashSet<Type> ShadowCloneComponentBlacklist = new HashSet<Type> {
        typeof(Camera), typeof(FlareLayer), typeof(AudioSource), typeof(Light), typeof(ParticleSystemRenderer), typeof(Rigidbody), typeof(Joint)
    };
    [Header("Built-in inputs / Viseme")]
    public VisemeIndex Viseme;
    [Range(0, 15)] public int VisemeIdx;
    private int VisemeInt;
    [Tooltip("Voice amount from 0.0f to 1.0f for the current viseme")]
    [Range(0,1)] public float Voice;
    [Header("Built-in inputs / Hand Gestures")]
    public GestureIndex GestureLeft;
    [Range(0, 9)] public int GestureLeftIdx;
    private char GestureLeftIdxInt;
    [Range(0, 1)] public float GestureLeftWeight;
    private float OldGestureLeftWeight;
    public GestureIndex GestureRight;
    [Range(0, 9)] public int GestureRightIdx;
    private char GestureRightIdxInt;
    [Range(0, 1)] public float GestureRightWeight;
    private float OldGestureRightWeight;
    [Header("Built-in inputs / Locomotion")]
    public Vector3 Velocity;
    [Range(-400, 400)] public float AngularY;
    [Range(0, 1)] public float Upright;
    public bool Grounded;
    public bool Jump;
    public float JumpPower = 5;
    public float RunSpeed = 0.0f;
    private bool WasJump;
    private Vector3 JumpingHeight;
    private Vector3 JumpingVelocity;
    private bool PrevSeated, PrevTPoseCalibration, PrevIKPoseCalibration;
    public bool Seated;
    public bool AFK;
    public bool TPoseCalibration;
    public bool IKPoseCalibration;
    [Header("Built-in inputs / Tracking Setup and Other")]
    public TrackingTypeIndex TrackingType;
    [Range(0, 6)] public int TrackingTypeIdx;
    private char TrackingTypeIdxInt;
    public bool VRMode;
    public bool MuteSelf;
    private bool MuteTogglerOn;
    public bool InStation;
    [HideInInspector] public int AvatarVersion = 3;

    [Header("Output State (Read-only)")]
    public bool IsLocal;
    [HideInInspector] public bool IsMirrorClone;
    [HideInInspector] public bool IsShadowClone;
    public bool LocomotionIsDisabled;

    [Serializable]
    public struct IKTrackingOutput {
        public Vector3 HeadRelativeViewPosition;
        public Vector3 ViewPosition;
        public float AvatarScaleFactorGuess;
        public VRCAnimatorTrackingControl.TrackingType trackingHead;
        public VRCAnimatorTrackingControl.TrackingType trackingLeftHand;
        public VRCAnimatorTrackingControl.TrackingType trackingRightHand;
        public VRCAnimatorTrackingControl.TrackingType trackingHip;
        public VRCAnimatorTrackingControl.TrackingType trackingLeftFoot;
        public VRCAnimatorTrackingControl.TrackingType trackingRightFoot;
        public VRCAnimatorTrackingControl.TrackingType trackingLeftFingers;
        public VRCAnimatorTrackingControl.TrackingType trackingRightFingers;
        public VRCAnimatorTrackingControl.TrackingType trackingEyesAndEyelids;
        public VRCAnimatorTrackingControl.TrackingType trackingMouthAndJaw;
    }
    public IKTrackingOutput IKTrackingOutputData;

    [Serializable]
    public class FloatParam
    {
        [HideInInspector] public string stageName;
        public string name;
        [HideInInspector] public bool synced;
        [Range(-1, 1)] public float expressionValue;
        [HideInInspector] public float lastExpressionValue_;
        [Range(-1, 1)] public float value;
        [HideInInspector] private float exportedValue_;
        public float exportedValue {
            get {
                return exportedValue_;
            } set {
                this.exportedValue_ = value;
                this.value = value;
                this.lastExpressionValue_ = value;
                this.expressionValue = value;
            }
        }
        [HideInInspector] public float lastValue;
    }
    [Header("User-generated inputs")]
    public List<FloatParam> Floats = new List<FloatParam>();
    public Dictionary<string, int> FloatToIndex = new Dictionary<string, int>();

    [Serializable]
    public class IntParam
    {
        [HideInInspector] public string stageName;
        public string name;
        [HideInInspector] public bool synced;
        public int value;
        [HideInInspector] public int lastValue;
    }
    public List<IntParam> Ints = new List<IntParam>();
    public Dictionary<string, int> IntToIndex = new Dictionary<string, int>();

    [Serializable]
    public class BoolParam
    {
        [HideInInspector] public string stageName;

        public string name;
        [HideInInspector] public bool synced;
        public bool value;
        [HideInInspector] public bool lastValue;
        [HideInInspector] public bool[] hasTrigger;
        [HideInInspector] public bool[] hasBool;
    }
    public List<BoolParam> Bools = new List<BoolParam>();
    public Dictionary<string, int> BoolToIndex = new Dictionary<string, int>();

    public Dictionary<string, string> StageParamterToBuiltin = new Dictionary<string, string>();


    static public Dictionary<Animator, LyumaAv3Runtime> animatorToTopLevelRuntime = new Dictionary<Animator, LyumaAv3Runtime>();
    private HashSet<Animator> attachedAnimators;
    const float BASE_HEIGHT = 1.4f;


    public IEnumerator DelayedEnterPoseSpace(bool setView, float time) {
        yield return new WaitForSeconds(time);
        if (setView) {
            Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
            if (head != null) {
                IKTrackingOutputData.ViewPosition = animator.transform.InverseTransformPoint(head.TransformPoint(IKTrackingOutputData.HeadRelativeViewPosition));
            }
        } else {
            IKTrackingOutputData.ViewPosition = avadesc.ViewPosition;
        }
    }

    class BlendingState {
        float startWeight;
        float goalWeight;
        float blendStartTime;
        float blendDuration;
        public bool blending;

        public float UpdateBlending() {
            if (blendDuration <= 0) {
                blending = false;
                return goalWeight;
            }
            float amt = (Time.time - blendStartTime) / blendDuration;
            if (amt >= 1) {
                blending = false;
                return goalWeight;
            }
            return Mathf.Lerp(startWeight, goalWeight, amt);
        }
        public void StartBlend(float startWeight, float goalWeight, float duration) {
            this.startWeight = startWeight;
            this.blendDuration = duration;
            this.blendStartTime = Time.time;
            this.goalWeight = goalWeight;
            this.blending = true;
        }
    }
    class PlayableBlendingState : BlendingState {
        public List<BlendingState> layerBlends = new List<BlendingState>();

    }
    List<PlayableBlendingState> playableBlendingStates = new List<PlayableBlendingState>();

    static HashSet<Animator> issuedWarningAnimators = new HashSet<Animator>();
    static bool getTopLevelRuntime(string component, Animator innerAnimator, out LyumaAv3Runtime runtime) {
        if (animatorToTopLevelRuntime.TryGetValue(innerAnimator, out runtime)) {
            return true;
        }
        Transform transform = innerAnimator.transform;
        while (transform != null && runtime == null) {
            runtime = transform.GetComponent<LyumaAv3Runtime>();
            transform = transform.parent;
        }
        if (runtime != null) {
            if (runtime.attachedAnimators != null) {
                Debug.Log("[" + component + "]: " + innerAnimator + " found parent runtime after it was Awoken! Adding to cache. Did you move me?");
                animatorToTopLevelRuntime.Add(innerAnimator, runtime);
                runtime.attachedAnimators.Add(innerAnimator);
            } else {
                Debug.Log("[" + component + "]: " + innerAnimator + " found parent runtime without being Awoken! Wakey Wakey...", runtime);
                runtime.Awake();
            }
            return true;
        }

        if (!issuedWarningAnimators.Contains(innerAnimator))
        {
            issuedWarningAnimators.Add(innerAnimator);
            Debug.LogWarning("[" + component + "]: outermost Animator is not known: " + innerAnimator + ". If you changed something, consider resetting avatar", innerAnimator);
        }

        return false;
    }

    float getAdjustedParameterAsFloat(string paramName, bool convertRange=false, float srcMin=0.0f, float srcMax=0.0f, float dstMin=0.0f, float dstMax=0.0f) {
        float newValue = 0;
        int idx;
        if (FloatToIndex.TryGetValue(paramName, out idx)) {
            newValue = Floats[idx].exportedValue;
        } else if (IntToIndex.TryGetValue(paramName, out idx)) {
            newValue = (float)Ints[idx].value;
        } else if (BoolToIndex.TryGetValue(paramName, out idx)) {
            newValue = Bools[idx].value ? 1.0f : 0.0f;
        }
        if (convertRange) {
            if (dstMax != dstMin) {
                newValue = Mathf.Lerp(dstMin, dstMax, Mathf.Clamp01(Mathf.InverseLerp(srcMin, srcMax, newValue)));
            } else {
                newValue = dstMin;
            }
        }
        return newValue;
    }
    

    void OnDestroy () {
        if (this.playableGraph.IsValid()) {
            this.playableGraph.Destroy();
        }
        if (attachedAnimators != null) {
            foreach (var anim in attachedAnimators) {
                LyumaAv3Runtime runtime;
                if (animatorToTopLevelRuntime.TryGetValue(anim, out runtime) && runtime == this)
                {
                    animatorToTopLevelRuntime.Remove(anim);
                }
            }
        }
        if (animator != null) {
            if (animator.playableGraph.IsValid())
            {
                animator.playableGraph.Destroy();
            }
            animator.runtimeAnimatorController = origAnimatorController;
        }
    }

    void Awake()
    {
        if (AvatarSyncSource != null && OriginalSourceClone == null) {
            Debug.Log("Awake returning early for " + gameObject.name, this);
            return;
        }
        if (attachedAnimators != null) {
            Debug.Log("Deduplicating Awake() call if we already got awoken by our children.", this);
            return;
        }

        // Debug.Log("AWOKEN " + gameObject.name, this);
        attachedAnimators = new HashSet<Animator>();
        if (AvatarSyncSource == null) {
            var oml = GetComponent<UnityEngine.AI.OffMeshLink>();
            if (oml != null && oml.startTransform != null) {
                GameObject.DestroyImmediate(oml);
            }
            Transform transform = this.transform;
            SourceObjectPath = "";
            while (transform != null) {
                SourceObjectPath = "/" + transform.name + SourceObjectPath;
                transform = transform.parent;
            }
            AvatarSyncSource = this;
        }
        else
        {
            AvatarSyncSource = GameObject.Find(SourceObjectPath).GetComponent<LyumaAv3Runtime>();
        }

        animator = this.gameObject.GetOrAddComponent<Animator>();
        if (animatorAvatar != null && animator.avatar == null) {
            animator.avatar = animatorAvatar;
        } else {
            animatorAvatar = animator.avatar;
        }
        // Default values.
        Grounded = true;
        Upright = 1.0f;
        if (!animator.isHuman) {
            TrackingType = TrackingTypeIndex.GenericRig;
        } else if (!VRMode) {
            TrackingType = TrackingTypeIndex.HeadHands;
        }
        avadesc = this.gameObject.GetComponent<VRCAvatarDescriptor>();
        if (avadesc.VisemeSkinnedMesh == null) {
            mouthOpenBlendShapeIdx = -1;
            visemeBlendShapeIdxs = new int[0];
        } else {
            mouthOpenBlendShapeIdx = avadesc.VisemeSkinnedMesh.sharedMesh.GetBlendShapeIndex(avadesc.MouthOpenBlendShapeName);
            visemeBlendShapeIdxs = new int[avadesc.VisemeBlendShapes == null ? 0 : avadesc.VisemeBlendShapes.Length];
            if (avadesc.VisemeBlendShapes != null) {
                for (int i = 0; i < avadesc.VisemeBlendShapes.Length; i++) {
                    visemeBlendShapeIdxs[i] = avadesc.VisemeSkinnedMesh.sharedMesh.GetBlendShapeIndex(avadesc.VisemeBlendShapes[i]);
                }
            }
        }
        bool shouldClone = false;
        if (OriginalSourceClone == null) {
            OriginalSourceClone = this;
            shouldClone = true;
        }
        if (shouldClone && GetComponent<PipelineSaver>() == null) {
            GameObject cloned = GameObject.Instantiate(gameObject);
            cloned.hideFlags = HideFlags.HideAndDontSave;
            cloned.SetActive(false);
            OriginalSourceClone = cloned.GetComponent<LyumaAv3Runtime>();
            Debug.Log("Spawned a hidden source clone " + OriginalSourceClone, OriginalSourceClone);
            OriginalSourceClone.OriginalSourceClone = OriginalSourceClone;
        }
        foreach (var smr in gameObject.GetComponentsInChildren<SkinnedMeshRenderer>(true)) {
            smr.updateWhenOffscreen = (AvatarSyncSource == this || IsMirrorClone || IsShadowClone);
        }
        int desiredLayer = 9;
        if (AvatarSyncSource == this) {
            desiredLayer = 10;
        }
        if (IsMirrorClone) {
            desiredLayer = 18;
        }
        if (IsShadowClone) {
            desiredLayer = 9; // the Shadowclone is always on playerLocal and never on UI Menu
        }
        if (gameObject.layer != 12 || desiredLayer == 18) {
            gameObject.layer = desiredLayer;
        }
        allTransforms = gameObject.GetComponentsInChildren<Transform>(true);
        foreach (Transform t in allTransforms) {
            if (t.gameObject.layer != 12 || desiredLayer == 18) {
                t.gameObject.layer = desiredLayer;
            }
        }

        InitializeAnimator();
        if (addRuntimeDelegate != null) {
            addRuntimeDelegate(this);
        }
        if (AvatarSyncSource == this) {
            CreateAv3MenuComponent();
        }
        if (this.AvatarSyncSource != this || IsMirrorClone || IsShadowClone) {
            PrevAnimatorToViewLiteParamsShow0 = (char)(int)ViewAnimatorOnlyNoParams;
        }
        if (!IsMirrorClone && !IsShadowClone && AvatarSyncSource == this) {
            var pipelineManager = avadesc.GetComponent<VRC.Core.PipelineManager>();
            string avatarid = pipelineManager != null ? pipelineManager.blueprintId : null;
        }
    }

    public void CreateMirrorClone() {
        if (AvatarSyncSource == this && GetComponent<PipelineSaver>() == null) {
            OriginalSourceClone.IsMirrorClone = true;
            MirrorClone = GameObject.Instantiate(OriginalSourceClone.gameObject).GetComponent<LyumaAv3Runtime>();
            MirrorClone.GetComponent<Animator>().avatar = null;
            OriginalSourceClone.IsMirrorClone = false;
            GameObject o = MirrorClone.gameObject;
            o.name = gameObject.name + " (MirrorReflection)";
            o.SetActive(true);
            allMirrorTransforms = MirrorClone.gameObject.GetComponentsInChildren<Transform>(true);
            foreach (Component component in MirrorClone.gameObject.GetComponentsInChildren<Component>(true)) {
                if (MirrorCloneComponentBlacklist.Contains(component.GetType()) || component.GetType().ToString().Contains("DynamicBone")
                         || component.GetType().ToString().Contains("VRCContact") || component.GetType().ToString().Contains("VRCPhysBone")) {
                    UnityEngine.Object.Destroy(component);
                }
            }
        }
    }

    public void CreateShadowClone() {
        if (AvatarSyncSource == this && GetComponent<PipelineSaver>() == null) {
            OriginalSourceClone.IsShadowClone = true;
            ShadowClone = GameObject.Instantiate(OriginalSourceClone.gameObject).GetComponent<LyumaAv3Runtime>();
            ShadowClone.GetComponent<Animator>().avatar = null;
            OriginalSourceClone.IsShadowClone = false;
            GameObject o = ShadowClone.gameObject;
            o.name = gameObject.name + " (ShadowClone)";
            o.SetActive(true);
            allShadowTransforms = ShadowClone.gameObject.GetComponentsInChildren<Transform>(true);
            foreach (Component component in ShadowClone.gameObject.GetComponentsInChildren<Component>(true)) {
                if (ShadowCloneComponentBlacklist.Contains(component.GetType()) || component.GetType().ToString().Contains("DynamicBone")
                         || component.GetType().ToString().Contains("VRCContact") || component.GetType().ToString().Contains("VRCPhysBone")) {
                    UnityEngine.Object.Destroy(component);
                    continue;
                }
                if (component.GetType() == typeof(SkinnedMeshRenderer) || component.GetType() == typeof(MeshRenderer)) {
                    Renderer renderer = component as Renderer;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly; // ShadowCastingMode.TwoSided isn't accounted for and does not work locally
                }
            }
            foreach (Renderer renderer in gameObject.GetComponentsInChildren<Renderer>(true)) {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; // ShadowCastingMode.TwoSided isn't accounted for and does not work locally
            }
        }
    }

    private void InitializeAnimator()
    {
        ResetAvatar = false;
        PrevAnimatorToDebug = (char)(int)DebugDuplicateAnimator;
        ViewAnimatorOnlyNoParams = DebugDuplicateAnimator;

        animator = this.gameObject.GetOrAddComponent<Animator>();
        animator.avatar = animatorAvatar;
        animator.applyRootMotion = false;
        animator.updateMode = AnimatorUpdateMode.Normal;
        animator.cullingMode = (this == AvatarSyncSource || IsMirrorClone || IsShadowClone) ? AnimatorCullingMode.AlwaysAnimate : AnimatorCullingMode.CullCompletely;
        animator.runtimeAnimatorController = null;

        avadesc = this.gameObject.GetComponent<VRCAvatarDescriptor>();
        IKTrackingOutputData.ViewPosition = avadesc.ViewPosition;
        IKTrackingOutputData.AvatarScaleFactorGuess = IKTrackingOutputData.ViewPosition.magnitude / BASE_HEIGHT; // mostly guessing...
        IKTrackingOutputData.HeadRelativeViewPosition = IKTrackingOutputData.ViewPosition;
        if (animator.avatar != null)
        {
            Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
            if (head != null) {
                IKTrackingOutputData.HeadRelativeViewPosition = head.InverseTransformPoint(animator.transform.TransformPoint(IKTrackingOutputData.ViewPosition));
            }
        }
        expressionsMenu = avadesc.expressionsMenu;
        stageParameters = avadesc.expressionParameters;
        if (origAnimatorController != null) {
            origAnimatorController = animator.runtimeAnimatorController;
        }

        VRCAvatarDescriptor.CustomAnimLayer[] baselayers = avadesc.baseAnimationLayers;
        VRCAvatarDescriptor.CustomAnimLayer[] speciallayers = avadesc.specialAnimationLayers;
        List<VRCAvatarDescriptor.CustomAnimLayer> allLayers = new List<VRCAvatarDescriptor.CustomAnimLayer>();
        // foreach (VRCAvatarDescriptor.CustomAnimLayer cal in baselayers) {
        //     if (AnimatorToDebug == cal.type) {
        //         allLayers.Add(cal);
        //     }
        // }
        // foreach (VRCAvatarDescriptor.CustomAnimLayer cal in speciallayers) {
        //     if (AnimatorToDebug == cal.type) {
        //         allLayers.Add(cal);
        //     }
        // }
        int i = 0;
        if (DebugDuplicateAnimator != VRCAvatarDescriptor.AnimLayerType.Base && !IsMirrorClone && !IsShadowClone) {
            foreach (VRCAvatarDescriptor.CustomAnimLayer cal in baselayers) {
                if (DebugDuplicateAnimator == cal.type) {
                    i++;
                    allLayers.Add(cal);
                    break;
                }
            }
            foreach (VRCAvatarDescriptor.CustomAnimLayer cal in speciallayers) {
                if (DebugDuplicateAnimator == cal.type) {
                    i++;
                    allLayers.Add(cal);
                    break;
                }
            }
            // WE ADD ALL THE LAYERS A SECOND TIME BECAUSE!
            // Add and Random Parameter drivers are not idepotent.
            // To solve this, we ignore every other invocation.
            // Therefore, we must add all layers twice, not just the one we are debugging...???
            foreach (VRCAvatarDescriptor.CustomAnimLayer cal in baselayers) {
                if (DebugDuplicateAnimator != cal.type) {
                    i++;
                    allLayers.Add(cal);
                }
            }
            foreach (VRCAvatarDescriptor.CustomAnimLayer cal in speciallayers) {
                if (DebugDuplicateAnimator != cal.type) {
                    i++;
                    allLayers.Add(cal);
                }
            }
        }
        int dupeOffset = i;
        if (!IsMirrorClone && !IsShadowClone) {
            foreach (VRCAvatarDescriptor.CustomAnimLayer cal in baselayers) {
                if (cal.type == VRCAvatarDescriptor.AnimLayerType.Base || cal.type == VRCAvatarDescriptor.AnimLayerType.Additive) {
                    i++;
                    allLayers.Add(cal);
                }
            }
            foreach (VRCAvatarDescriptor.CustomAnimLayer cal in speciallayers) {
                i++;
                allLayers.Add(cal);
            }
        }
        foreach (VRCAvatarDescriptor.CustomAnimLayer cal in baselayers) {
            if (IsMirrorClone || IsShadowClone) {
                if (cal.type == VRCAvatarDescriptor.AnimLayerType.FX) {
                    i++;
                    allLayers.Add(cal);
                }
            } else if (!(cal.type == VRCAvatarDescriptor.AnimLayerType.Base || cal.type == VRCAvatarDescriptor.AnimLayerType.Additive)) {
                i++;
                allLayers.Add(cal);
            }
        }

        if (playableGraph.IsValid()) {
            playableGraph.Destroy();
        }
        playables.Clear();
        playableBlendingStates.Clear();

        for (i = 0; i < allLayers.Count; i++) {
            playables.Add(new AnimatorControllerPlayable());
            playableBlendingStates.Add(null);
        }

        actionIndex = fxIndex = gestureIndex = additiveIndex = sittingIndex = ikposeIndex = tposeIndex = -1;
        altActionIndex = altFXIndex = altGestureIndex = altAdditiveIndex = -1;

        foreach (var anim in attachedAnimators) {
            LyumaAv3Runtime runtime;
            if (animatorToTopLevelRuntime.TryGetValue(anim, out runtime) && runtime == this)
            {
                animatorToTopLevelRuntime.Remove(anim);
            }
        }
        attachedAnimators.Clear();
        Animator[] animators = this.gameObject.GetComponentsInChildren<Animator>(true);
        foreach (Animator anim in animators)
        {
            attachedAnimators.Add(anim);
            animatorToTopLevelRuntime.Add(anim, this);
        }

        Dictionary<string, float> stageNameToValue = EarlyRefreshExpressionParameters();
        if (animator.playableGraph.IsValid())
        {
            animator.playableGraph.Destroy();
        }
        // var director = avadesc.gameObject.GetComponent<PlayableDirector>();
        playableGraph = PlayableGraph.Create("LyumaAvatarRuntime - " + this.gameObject.name);
        var externalOutput = AnimationPlayableOutput.Create(playableGraph, "ExternalAnimator", animator);
        playableMixer = AnimationLayerMixerPlayable.Create(playableGraph, allLayers.Count + 1);
        externalOutput.SetSourcePlayable(playableMixer);
        animator.applyRootMotion = false;

        i = 0;
        // playableMixer.ConnectInput(0, AnimatorControllerPlayable.Create(playableGraph, allLayers[layerToDebug - 1].animatorController), 0, 0);
        foreach (VRCAvatarDescriptor.CustomAnimLayer vrcAnimLayer in allLayers)
        {
            i++; // Ignore zeroth layer.
            bool additive = (vrcAnimLayer.type == VRCAvatarDescriptor.AnimLayerType.Additive);
            RuntimeAnimatorController ac = null;
            AvatarMask mask;
            if (vrcAnimLayer.isDefault) {
                ac = animLayerToDefaultController[vrcAnimLayer.type];
                mask = animLayerToDefaultAvaMask[vrcAnimLayer.type];
            } else
            {
                ac = vrcAnimLayer.animatorController;
                mask = vrcAnimLayer.mask;
                if (mask == null && vrcAnimLayer.type == VRCAvatarDescriptor.AnimLayerType.FX) {
                    mask = animLayerToDefaultAvaMask[vrcAnimLayer.type]; // When empty, defaults to a mask that prevents muscle overrides.
                }
            }
            if (ac == null) {
                Debug.Log(vrcAnimLayer.type + " controller is null: continue.");
                // i was incremented, but one of the playableMixer inputs is left unconnected.
                continue;
            }
            allControllers[vrcAnimLayer.type] = ac;
            AnimatorControllerPlayable humanAnimatorPlayable = AnimatorControllerPlayable.Create(playableGraph, ac);
            PlayableBlendingState pbs = new PlayableBlendingState();
            for (int j = 0; j < humanAnimatorPlayable.GetLayerCount(); j++)
            {
                pbs.layerBlends.Add(new BlendingState());
            }

            // If we are debugging a particular layer, we must put that first.
            // The Animator Controller window only shows the first layer.
            int effectiveIdx = i;

            playableMixer.ConnectInput((int)effectiveIdx, humanAnimatorPlayable, 0, 1);
            playables[effectiveIdx - 1] = humanAnimatorPlayable;
            playableBlendingStates[effectiveIdx - 1] = pbs;
            if (vrcAnimLayer.type == VRCAvatarDescriptor.AnimLayerType.Sitting) {
                if (i >= dupeOffset) {
                    sittingIndex = effectiveIdx - 1;
                }
                playableMixer.SetInputWeight(effectiveIdx, 0f);
            }
            if (vrcAnimLayer.type == VRCAvatarDescriptor.AnimLayerType.IKPose)
            {
                if (i >= dupeOffset) {
                    ikposeIndex = effectiveIdx - 1;
                }
                playableMixer.SetInputWeight(effectiveIdx, 0f);
            }
            if (vrcAnimLayer.type == VRCAvatarDescriptor.AnimLayerType.TPose)
            {
                if (i >= dupeOffset) {
                    tposeIndex = effectiveIdx - 1;
                }
                playableMixer.SetInputWeight(effectiveIdx, 0f);
            }
            if (vrcAnimLayer.type == VRCAvatarDescriptor.AnimLayerType.Action)
            {
                playableMixer.SetInputWeight(i, 0f);
                if (i < dupeOffset) {
                    altActionIndex = effectiveIdx - 1;
                } else {
                    actionIndex = effectiveIdx - 1;
                }

            }
            if (vrcAnimLayer.type == VRCAvatarDescriptor.AnimLayerType.Gesture) {
                if (i < dupeOffset) {
                    altGestureIndex = effectiveIdx - 1;
                } else {
                    gestureIndex = effectiveIdx - 1;
                }

            }
            if (vrcAnimLayer.type == VRCAvatarDescriptor.AnimLayerType.Additive)
            {
                if (i < dupeOffset) {
                    altAdditiveIndex = effectiveIdx - 1;
                } else {
                    additiveIndex = effectiveIdx - 1;
                }

            }
            if (vrcAnimLayer.type == VRCAvatarDescriptor.AnimLayerType.FX)
            {
                if (i < dupeOffset) {
                    altFXIndex = effectiveIdx - 1;
                } else {
                    fxIndex = effectiveIdx - 1;
                }
            }
            // AnimationControllerLayer acLayer = new AnimationControllerLayer()
            if (mask != null)
            {
                playableMixer.SetLayerMaskFromAvatarMask((uint)effectiveIdx, mask);
            }
            if (additive)
            {
                playableMixer.SetLayerAdditive((uint)effectiveIdx, true);
            }

            // Keep weight 1.0 if (i < dupeOffset).
            // Layers have incorrect AAP values if playable weight is 0.0...
            // and the duplicate layers will be overridden later anyway by Base.
        }

        LateRefreshExpressionParameters(stageNameToValue);

        // Plays the Graph.
        playableGraph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
        Debug.Log(this.name + " : " + GetType() + " awoken and ready to Play.", this);
        playableGraph.Play();
    }

    Dictionary<string, float> EarlyRefreshExpressionParameters() {
        Dictionary<string, float> stageNameToValue = new Dictionary<string, float>();
        if (IsLocal) {
            foreach (var val in Ints) {
                stageNameToValue[val.stageName] = val.value;
            }
            foreach (var val in Floats) {
                stageNameToValue[val.stageName] = val.exportedValue;
            }
            foreach (var val in Bools) {
                stageNameToValue[val.stageName] = val.value ? 1.0f : 0.0f;
            }
        }
        Ints.Clear();
        Bools.Clear();
        Floats.Clear();
        StageParamterToBuiltin.Clear();
        IntToIndex.Clear();
        FloatToIndex.Clear();
        BoolToIndex.Clear();
        playableParamterFloats.Clear();
        playableParamterIds.Clear();
        playableParamterInts.Clear();
        playableParamterBools.Clear();
        return stageNameToValue;
    }
    void LateRefreshExpressionParameters(Dictionary<string, float> stageNameToValue) {
        HashSet<string> usedparams = new HashSet<string>(BUILTIN_PARAMETERS);
        int i = 0;
        if (stageParameters != null)
        {
            int stageId = 0;
            foreach (var stageParam in stageParameters.parameters)
            {
                stageId++; // one-indexed
                if (stageParam.name == null || stageParam.name.Length == 0) {
                    continue;
                }
                string stageName = stageParam.name + (stageParam.saved ? " (saved/SYNCED)" : " (SYNCED)"); //"Stage" + stageId;
                float lastDefault = 0.0f;
                if (AvatarSyncSource == this) {
                    lastDefault = (stageParam.saved && KeepSavedParametersOnReset && stageNameToValue.ContainsKey(stageName) ? stageNameToValue[stageName] : stageParam.defaultValue);
                }
                StageParamterToBuiltin.Add(stageName, stageParam.name);
                if ((int)stageParam.valueType == 0)
                {
                    IntParam param = new IntParam();
                    param.stageName = stageName;
                    param.synced = true;
                    param.name = stageParam.name;
                    param.value = (int)lastDefault;
                    param.lastValue = 0;
                    IntToIndex[param.name] = Ints.Count;
                    Ints.Add(param);
                }
                else if ((int)stageParam.valueType == 1)
                {
                    FloatParam param = new FloatParam();
                    param.stageName = stageName;
                    param.synced = true;
                    param.name = stageParam.name;
                    param.value = lastDefault;
                    param.exportedValue = lastDefault;
                    param.lastValue = 0;
                    FloatToIndex[param.name] = Floats.Count;
                    Floats.Add(param);
                }
                else if ((int)stageParam.valueType == 2)
                {
                    BoolParam param = new BoolParam();
                    param.stageName = stageName;
                    param.synced = true;
                    param.name = stageParam.name;
                    param.value = lastDefault != 0.0;
                    param.lastValue = false;
                    param.hasBool = new bool[playables.Count];
                    param.hasTrigger = new bool[playables.Count];
                    BoolToIndex[param.name] = Bools.Count;
                    Bools.Add(param);
                }
                usedparams.Add(stageParam.name);
                i++;
            }
        } else {
            IntParam param = new IntParam();
            param.stageName = "VRCEmote";
            param.synced = true;
            param.name = "VRCEmote";
            Ints.Add(param);
            usedparams.Add("VRCEmote");
            FloatParam fparam = new FloatParam();
            fparam.stageName = "VRCFaceBlendH";
            fparam.synced = true;
            fparam.name = "VRCFaceBlendH";
            Floats.Add(fparam);
            usedparams.Add("VRCFaceBlendH");
            fparam = new FloatParam();
            fparam.stageName = "VRCFaceBlendV";
            fparam.synced = true;
            fparam.name = "VRCFaceBlendV";
            Floats.Add(fparam);
            usedparams.Add("VRCFaceBlendV");
        }

        //playableParamterIds
        int whichcontroller = 0;
        playableParamterIds.Clear();
        foreach (AnimatorControllerPlayable playable in playables) {
            Dictionary<string, int> parameterIndices = new Dictionary<string, int>();
            playableParamterInts.Add(new Dictionary<int, int>());
            playableParamterFloats.Add(new Dictionary<int, float>());
            playableParamterBools.Add(new Dictionary<int, bool>());
            // Debug.Log("SETUP index " + whichcontroller + " len " + playables.Count);
            playableParamterIds.Add(parameterIndices);
            int pcnt = playable.IsValid() ? playable.GetParameterCount() : 0;
            for (i = 0; i < pcnt; i++) {
                AnimatorControllerParameter aparam = playable.GetParameter(i);
                string actualName;
                if (!StageParamterToBuiltin.TryGetValue(aparam.name, out actualName)) {
                    actualName = aparam.name;
                }
                parameterIndices[actualName] = aparam.nameHash;
                if (usedparams.Contains(actualName)) {
                    if (BoolToIndex.ContainsKey(aparam.name) && aparam.type == AnimatorControllerParameterType.Bool) {
                        Bools[BoolToIndex[aparam.name]].hasBool[whichcontroller] = true;
                    }
                    if (BoolToIndex.ContainsKey(aparam.name) && aparam.type == AnimatorControllerParameterType.Trigger) {
                        Bools[BoolToIndex[aparam.name]].hasTrigger[whichcontroller] = true;
                    }
                    continue;
                }
                if (aparam.type == AnimatorControllerParameterType.Int) {
                    IntParam param = new IntParam();
                    param.stageName = aparam.name + " (local)";
                    param.synced = false;
                    param.name = aparam.name;
                    param.value = aparam.defaultInt;
                    param.lastValue = param.value;
                    IntToIndex[param.name] = Ints.Count;
                    Ints.Add(param);
                    usedparams.Add(aparam.name);
                } else if (aparam.type == AnimatorControllerParameterType.Float) {
                    FloatParam param = new FloatParam();
                    param.stageName = aparam.name + " (local)";
                    param.synced = false;
                    param.name = aparam.name;
                    param.value = aparam.defaultFloat;
                    param.exportedValue = aparam.defaultFloat;
                    param.lastValue = param.value;
                    FloatToIndex[param.name] = Floats.Count;
                    Floats.Add(param);
                    usedparams.Add(aparam.name);
                } else if (aparam.type == AnimatorControllerParameterType.Trigger || aparam.type == AnimatorControllerParameterType.Bool) {
                    BoolParam param = new BoolParam();
                    param.stageName = aparam.name + " (local)";
                    param.synced = false;
                    param.name = aparam.name;
                    param.value = aparam.defaultBool;
                    param.lastValue = param.value;
                    param.hasBool = new bool[playables.Count];
                    param.hasTrigger = new bool[playables.Count];
                    param.hasBool[whichcontroller] = aparam.type == AnimatorControllerParameterType.Bool;
                    param.hasTrigger[whichcontroller] = aparam.type == AnimatorControllerParameterType.Trigger;
                    BoolToIndex[param.name] = Bools.Count;
                    Bools.Add(param);
                    usedparams.Add(aparam.name);
                }
            }
            whichcontroller++;
        }
    }

    void CreateAv3MenuComponent() {
    }


    private bool isResetting;
    private bool isResettingHold;
    private bool isResettingSel;
    void LateUpdate() {
       
    }

    void FixedUpdate() {
      
    }

    private bool broadcastStartNextFrame;
    void OnEnable() {
      
    }

    void Update() {
        
    }

    // Update is called once per frame
    void NormalUpdate()
    {
    }

    float getObjectFloat(object o) {
        switch (o) {
            // case bool b:
            //     return b ? 1.0f : 0.0f;
            // case int i:
            //     return (float)i;
            // case long l:
            //     return (float)l;
            case float f:
                return f;
            // case double d:
            //     return (float)d;
        }
        return 0.0f;
    }
    int getObjectInt(object o) {
        switch (o) {
            // case bool b:
            //     return b ? 1 : 0;
            case int i:
                return i;
            // case long l:
            //     return (int)l;
            // case float f:
            //     return (int)f;
            // case double d:
            //     return (int)d;
        }
        return 0;
    }
    bool isObjectTrue(object o) {
        switch (o) {
            case bool b:
                return b;
            case int i:
                return i == 1;
            // case long l:
            //     return l == 1;
            // case float f:
            //     return f == 1.0f;
            // case double d:
            //     return d == 1.0;
        }
        return false;
    }

    public void GetOSCDataInto(Object messages) {
    }
    
    public void processOSCInputMessage(string ParamName, object arg0) {
    }

    public void processOSCVRCInputMessage(string ParamName, object arg0) {
        
    }
    public void HandleOSCMessages(Object messages) {
    }
}
