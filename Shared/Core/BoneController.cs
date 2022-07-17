using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using IllusionUtility.GetUtility;
using KKAPI;
using KKAPI.Chara;
using Manager;
using MessagePack;
using UnityEngine;
using ExtensibleSaveFormat;
using KKAPI.Maker;
using KKAPI.Utilities;
using UniRx;
#if AI || HS2
using AIChara;
#endif

namespace KKABMX.Core
{
#if KK || KKS
    using CoordinateType = ChaFileDefine.CoordinateType;
#elif EC
    using CoordinateType = KoikatsuCharaFile.ChaFileDefine.CoordinateType;
#elif AI || HS2
    /// <summary>
    /// Placeholder for AIS to keep the API compatibility
    /// </summary>
    public enum CoordinateType
    {
        /// <summary>
        /// Current coordinate in AIS
        /// </summary>
        Unknown = 0
    }
#endif

    /// <summary>
    /// Manages and applies bone modifiers for a single character.
    /// </summary>
    public class BoneController : CharaCustomFunctionController
    {
        private const string ExtDataBoneDataKey = "boneData";

        internal BoneFinder _boneSearcher { get; private set; }
        private bool? _baselineKnown;

        /// <summary>
        /// Trigger a full bone modifier refresh on the next update
        /// </summary>
        public bool NeedsFullRefresh { get; set; }
        /// <summary>
        /// Trigger all modifiers to collect new baselines on the next update
        /// </summary>
        public bool NeedsBaselineUpdate { get; set; }

        /// <summary>
        /// All bone modifiers assigned to this controller
        /// </summary>
        public List<BoneModifier> Modifiers { get; private set; } = new List<BoneModifier>();

        /// <summary>
        /// Additional effects that other plugins can apply to a character
        /// </summary>
        public IEnumerable<BoneEffect> AdditionalBoneEffects => _additionalBoneEffects;
        private readonly List<BoneEffect> _additionalBoneEffects = new List<BoneEffect>();
        private bool _isDuringHScene;

        /// <summary>
        /// Signals that new modifier data was loaded and custom Modifiers and AdditionalBoneEffects might need to be updated
        /// </summary>
        public event EventHandler NewDataLoaded;

#if EC
        /// <summary>
        /// Placeholder to keep the API compatibility, all coordinate logic targets the KK School01 slot
        /// </summary>
        public BehaviorSubject<CoordinateType> CurrentCoordinate = new BehaviorSubject<CoordinateType>(CoordinateType.School01);
#elif AI || HS2
        /// <summary>
        /// Placeholder to keep the API compatibility
        /// </summary>
        public BehaviorSubject<CoordinateType> CurrentCoordinate = new BehaviorSubject<CoordinateType>(CoordinateType.Unknown);
#endif

        /// <summary>
        /// Add a new bone modifier. Make sure it doesn't exist yet.
        /// </summary>
        public void AddModifier(BoneModifier bone)
        {
            if (bone == null) throw new ArgumentNullException(nameof(bone));
            Modifiers.Add(bone);
            ModifiersFillInTransforms();
            bone.CollectBaseline();
        }

        /// <summary>
        /// Add specified bone effect and update state to make it work. If the effect is already added then this does nothing.
        /// </summary>
        public void AddBoneEffect(BoneEffect effect)
        {
            if (_additionalBoneEffects.Contains(effect)) return;

            _additionalBoneEffects.Add(effect);
        }

        /// <inheritdoc cref="GetModifier(string,BoneLocation)"/>
        [Obsolete]
        public BoneModifier GetModifier(string boneName) => GetModifier(boneName, BoneLocation.Unknown);

        /// <summary>
        /// Get a modifier if it exists.
        /// </summary>
        /// <param name="boneName">Name of the bone that the modifier targets</param>
        /// <param name="location">Where the bone is located</param>
        public BoneModifier GetModifier(string boneName, BoneLocation location)
        {
            if (boneName == null) throw new ArgumentNullException(nameof(boneName));
            for (var i = 0; i < Modifiers.Count; i++)
            {
                var x = Modifiers[i];
                if ((location == BoneLocation.Unknown || location == x.BoneLocation) && x.BoneName == boneName) return x;
            }

            return null;
        }

        /// <summary>
        /// Removes the specified modifier and resets the affected bone to its original state
        /// </summary>
        /// <param name="modifier">Modifier added to this controller</param>
        public void RemoveModifier(BoneModifier modifier)
        {
            modifier.Reset();
            Modifiers.Remove(modifier);

            ChaControl.updateShapeFace = true;
            ChaControl.updateShapeBody = true;
        }

        /// <summary>
        /// Get all transform names under the character object that could be bones (excludes accessories).
        /// Warning: Expensive to run, ToList the result and cache it if you want to reuse it!
        /// </summary>
        public IEnumerable<string> GetAllPossibleBoneNames() => GetAllPossibleBoneNames(ChaControl.objTop);

        /// <summary>
        /// Get all transform names under the rootObject that could be bones (could be from BodyTop or objAccessory, BodyTop excludes accessories).
        /// Warning: Expensive to run, ToList the result and cache it if you want to reuse it!
        /// </summary>
        public IEnumerable<string> GetAllPossibleBoneNames(GameObject rootObject)
        {
            return _boneSearcher.CreateBoneDic(rootObject).Keys
#if AI || HS2
                                .Where(x => !x.StartsWith("f_t_", StringComparison.Ordinal) && !x.StartsWith("f_pv_", StringComparison.Ordinal) && !x.StartsWith("f_k_", StringComparison.Ordinal));
#elif KK || EC || KKS
                                .Where(x => !x.StartsWith("cf_t_", StringComparison.Ordinal) && !x.StartsWith("cf_pv_", StringComparison.Ordinal));
#endif
        }

#if !EC && !AI && !HS2 //No coordinate saving in AIS
        protected override void OnCoordinateBeingLoaded(ChaFileCoordinate coordinate, bool maintainState)
        {
            if (maintainState) return;

            var currentCoord = CurrentCoordinate.Value;

            // Clear previous data for this coordinate from coord specific modifiers
            foreach (var modifier in Modifiers.Where(x => x.IsCoordinateSpecific()))
                modifier.GetModifier(currentCoord).Clear();

            var data = GetCoordinateExtendedData(coordinate);
            var modifiers = ReadCoordModifiers(data);
            foreach (var modifier in modifiers)
            {
                var target = GetModifier(modifier.BoneName, modifier.BoneLocation);
                if (target == null)
                {
                    // Add any missing modifiers
                    target = new BoneModifier(modifier.BoneName, modifier.BoneLocation);
                    AddModifier(target);
                }
                target.MakeCoordinateSpecific(ChaFileControl.coordinate.Length);
                target.CoordinateModifiers[(int)currentCoord] = modifier.CoordinateModifiers[0];
            }

            StartCoroutine(OnDataChangedCo());
        }

        internal static List<BoneModifier> ReadCoordModifiers(PluginData data)
        {
            if (data != null)
            {
                try
                {
                    switch (data.version)
                    {
                        case 3:
                            return LZ4MessagePackSerializer.Deserialize<List<BoneModifier>>((byte[])data.data[ExtDataBoneDataKey]);

                        case 2:
                            return LZ4MessagePackSerializer.Deserialize<Dictionary<string, BoneModifierData>>((byte[])data.data[ExtDataBoneDataKey])
                                                           .Select(x => new BoneModifier(x.Key, BoneLocation.Unknown, new[] { x.Value }))
                                                           .ToList();
                        default:
                            throw new NotSupportedException($"Save version {data.version} is not supported");
                    }
                }
                catch (Exception ex)
                {
                    KKABMX_Core.Logger.LogError("[KKABMX] Failed to load coordinate extended data - " + ex);
                }
            }

            return new List<BoneModifier>();
        }

        protected override void OnCoordinateBeingSaved(ChaFileCoordinate coordinate)
        {
            var currentCoord = CurrentCoordinate.Value;
            var toSave = Modifiers.Where(x => !x.IsEmpty() && x.IsCoordinateSpecific() && x.BoneTransform != null)
                                  .Select(x => new BoneModifier(x.BoneName, x.BoneLocation, new[] { x.GetModifier(currentCoord) }))
                                  .ToList();

            if (toSave.Count == 0)
                SetCoordinateExtendedData(coordinate, null);
            else
            {
                var pluginData = new PluginData { version = 3 };
                pluginData.data.Add(ExtDataBoneDataKey, LZ4MessagePackSerializer.Serialize(toSave));
                SetCoordinateExtendedData(coordinate, pluginData);
            }
        }
#endif

        /// <inheritdoc />
        protected override void OnCardBeingSaved(GameMode currentGameMode)
        {
            var data = SaveModifiers(Modifiers);
            SetExtendedData(data);
        }

        internal void RevertChanges() => OnReload(KoikatuAPI.GetCurrentGameMode(), false);

        /// <inheritdoc />
        protected override void OnReload(GameMode currentGameMode, bool maintainState)
        {
            foreach (var modifier in Modifiers)
                modifier.Reset();

            // Stop baseline collection if it's running
            StopAllCoroutines();
            _baselineKnown = false;

            var loadClothes = MakerAPI.GetCharacterLoadFlags()?.Clothes != false;
            var loadBody = GUI.KKABMX_GUI.LoadBody;
            var loadFace = GUI.KKABMX_GUI.LoadFace;
            if (!maintainState && (loadBody || loadFace || loadClothes))
            {
                var data = GetExtendedData();
                var newModifiers = ReadModifiers(data);

                if (loadBody && loadFace && loadClothes)
                {
                    Modifiers = newModifiers;
                }
                else
                {
                    if (loadBody && loadFace)
                    {
                        Modifiers.RemoveAll(x => x.BoneLocation < BoneLocation.Accessory);
                        Modifiers.AddRange(newModifiers.Where(x => x.BoneLocation < BoneLocation.Accessory));
                    }
                    else
                    {
#if AI || HS2
                        var headRoot = transform.FindLoop("cf_J_Head"); //todo use objHead and walk up the parents to find this to improve perf?
#else
                        var headRoot = transform.FindLoop("cf_j_head");
#endif
                        var headBones = new HashSet<string>(headRoot.GetComponentsInChildren<Transform>().Select(x => x.name));
                        headBones.Add(headRoot.name);
                        if (loadFace)
                        {
                            Modifiers.RemoveAll(x => x.BoneLocation < BoneLocation.Accessory && headBones.Contains(x.BoneName));
                            Modifiers.AddRange(newModifiers.Where(x => x.BoneLocation < BoneLocation.Accessory && headBones.Contains(x.BoneName)));
                        }
                        else if (loadBody)
                        {
                            var bodyBones = new HashSet<string>(transform.FindLoop("BodyTop").GetComponentsInChildren<Transform>().Select(x => x.name).Except(headBones));

                            Modifiers.RemoveAll(x => x.BoneLocation < BoneLocation.Accessory && bodyBones.Contains(x.BoneName));
                            Modifiers.AddRange(newModifiers.Where(x => x.BoneLocation < BoneLocation.Accessory && bodyBones.Contains(x.BoneName)));
                        }
                    }

                    if (loadClothes)
                    {
                        Modifiers.RemoveAll(x => x.BoneLocation >= BoneLocation.Accessory);
                        Modifiers.AddRange(newModifiers.Where(x => x.BoneLocation >= BoneLocation.Accessory));
                    }
                }
            }

            StartCoroutine(OnDataChangedCo());
        }

        internal static List<BoneModifier> ReadModifiers(PluginData data)
        {
            if (data != null)
            {
                try
                {
                    switch (data.version)
                    {
                        case 2:
                            return LZ4MessagePackSerializer.Deserialize<List<BoneModifier>>((byte[])data.data[ExtDataBoneDataKey]);
#if KK || EC || KKS
                        case 1:
                            KKABMX_Core.Logger.LogDebug("[KKABMX] Loading legacy embedded ABM data");
                            return OldDataConverter.MigrateOldExtData(data);
#endif

                        default:
                            throw new NotSupportedException($"Save version {data.version} is not supported");
                    }
                }
                catch (Exception ex)
                {
                    KKABMX_Core.Logger.LogError("[KKABMX] Failed to load extended data - " + ex);
                }
            }
            return new List<BoneModifier>();
        }

        internal static PluginData SaveModifiers(List<BoneModifier> modifiers)
        {
            var toSave = modifiers.Where(x => !x.IsEmpty() && x.BoneTransform != null).ToList();

            if (toSave.Count == 0)
                return null;

            var data = new PluginData { version = 2 };
            data.data.Add(ExtDataBoneDataKey, LZ4MessagePackSerializer.Serialize(toSave));
            return data;
        }

        /// <inheritdoc />
        protected override void Start()
        {
            _boneSearcher = new BoneFinder(ChaControl);
            base.Start();
            CurrentCoordinate.Subscribe(_ => StartCoroutine(OnDataChangedCo()));
#if KK // hs2 ais is HScene //todo KKS full game
            _isDuringHScene = "H".Equals(Scene.Instance.LoadSceneName, StringComparison.Ordinal);
#endif
        }

        private void LateUpdate()
        {
            if (NeedsFullRefresh)
            {
                OnReload(KoikatuAPI.GetCurrentGameMode(), true);
                NeedsFullRefresh = false;
                return;
            }

            if (_baselineKnown == true)
            {
                if (NeedsBaselineUpdate)
                    UpdateBaseline();

                ApplyEffects();
            }
            else if (_baselineKnown == false)
            {
                _baselineKnown = null;
                CollectBaseline();
            }

            NeedsBaselineUpdate = false;
        }

        private readonly Dictionary<BoneModifier, List<BoneModifierData>> _effectsToUpdate = new Dictionary<BoneModifier, List<BoneModifierData>>();

        private void ApplyEffects()
        {
            _effectsToUpdate.Clear();

            foreach (var additionalBoneEffect in _additionalBoneEffects)
            {
                var affectedBones = additionalBoneEffect.GetAffectedBones(this);
                foreach (var affectedBone in affectedBones)
                {
                    var effect = additionalBoneEffect.GetEffect(affectedBone, this, CurrentCoordinate.Value);
                    if (effect != null && !effect.IsEmpty())
                    {
                        var modifier = GetModifier(affectedBone, BoneLocation.BodyTop); //todo allow targeting accessories?
                        if (modifier == null)
                        {
                            modifier = new BoneModifier(affectedBone, BoneLocation.BodyTop);
                            AddModifier(modifier);
                        }

                        if (!_effectsToUpdate.TryGetValue(modifier, out var list))
                        {
                            list = new List<BoneModifierData>();
                            _effectsToUpdate[modifier] = list;
                        }
                        list.Add(effect);
                    }
                }
            }

            for (var i = 0; i < Modifiers.Count; i++)
            {
                var modifier = Modifiers[i];
                if (!_effectsToUpdate.TryGetValue(modifier, out var list))
                {
                    // Clean up no longer necessary modifiers
                    // not used to reduce per-frame perf hit
                    //if (!GUI.KKABMX_AdvancedGUI.Enabled && modifier.IsEmpty())
                    //    RemoveModifier(modifier);
                }

                HandleDynamicBoneModifiers(modifier);

                modifier.Apply(CurrentCoordinate.Value, list, _isDuringHScene);
            }

            // Fix some bust physics issues
            // bug - causes gravity issues on its own
            if (Modifiers.Count > 0)
                ChaControl.UpdateBustGravity();
        }

        private IEnumerator OnDataChangedCo()
        {
            CleanEmptyModifiers();

            // Needed to let accessories load in
            yield return CoroutineUtils.WaitForEndOfFrame;

            ModifiersFillInTransforms();

            NeedsBaselineUpdate = false;

            NewDataLoaded?.Invoke(this, EventArgs.Empty);
        }

        private void CollectBaseline()
        {
            StartCoroutine(CollectBaselineCo());
        }

        private float? _previousAnimSpeed;
        private IEnumerator CollectBaselineCo()
        {
            do yield return CoroutineUtils.WaitForEndOfFrame;
            while (ChaControl.animBody == null);

            // Stop the animation to prevent bones from drifting while taking the measurement
            // Check if there's a speed already stored in case the previous run of this coroutine didn't finish
            if (!_previousAnimSpeed.HasValue) _previousAnimSpeed = ChaControl.animBody.speed;
            ChaControl.animBody.speed = 0;

#if KK || KKS || AI || HS2 // Only for studio
            var pvCopy = ChaControl.animBody.gameObject.GetComponent<Studio.PVCopy>();
            bool[] currentPvCopy = null;
            if (pvCopy != null)
            {
                var pvCount = pvCopy.pv.Length;
                currentPvCopy = new bool[pvCount];
                for (var i = 0; i < currentPvCopy.Length; i++)
                {
                    currentPvCopy[i] = pvCopy[i];
                    pvCopy[i] = false;
                }
            }
#endif

            yield return CoroutineUtils.WaitForEndOfFrame;

            // Ensure that the baseline is correct
            ChaControl.updateShapeFace = true;
            ChaControl.updateShapeBody = true;
            ChaControl.LateUpdateForce();

            ModifiersFillInTransforms();

            foreach (var modifier in Modifiers)
                modifier.CollectBaseline();

            _baselineKnown = true;

            yield return CoroutineUtils.WaitForEndOfFrame;

#if KK || KKS || AI || HS2 // Only for studio
            if (pvCopy != null)
            {
                var array = pvCopy.pv;
                var array2 = pvCopy.bone;
                for (var j = 0; j < currentPvCopy.Length; j++)
                {
                    if (currentPvCopy[j] && array2[j] && array[j])
                    {
                        array[j].transform.localScale = array2[j].transform.localScale;
                        array[j].transform.position = array2[j].transform.position;
                        array[j].transform.rotation = array2[j].transform.rotation;
                    }
                }
            }
#endif

            ChaControl.animBody.speed = _previousAnimSpeed ?? 1f;
            _previousAnimSpeed = null;
        }

        /// <summary>
        /// Partial baseline update.
        /// Needed mainly to prevent vanilla sliders in chara maker from being overriden by bone modifiers.
        /// </summary>
        private void UpdateBaseline()
        {
            var distSrc = ChaControl.sibFace.dictDst;
            var distSrc2 = ChaControl.sibBody.dictDst;
            var affectedBones = new HashSet<Transform>(distSrc.Concat(distSrc2).Select(x => x.Value.trfBone));
            var affectedModifiers = Modifiers.Where(x => affectedBones.Contains(x.BoneTransform)).ToList();

            // Prevent some scales from being added to the baseline, mostly skirt scale
            foreach (var boneModifier in affectedModifiers)
                boneModifier.Reset();

            // Force game to recalculate bone scales. Misses some so we need to reset above
            ChaControl.UpdateShapeFace();
            ChaControl.UpdateShapeBody();

            foreach (var boneModifier in affectedModifiers)
                boneModifier.CollectBaseline();
        }

        private void ModifiersFillInTransforms()
        {
            if (Modifiers.Count == 0) return;
            //todo somehow fill in the correct location, 
            foreach (var modifier in Modifiers)
            {
                if (modifier.BoneTransform != null) continue;
                _boneSearcher.AssignBone(modifier);
                // todo if (modifier.BoneTransform == null) then remove the modifier?
            }
        }

        internal void CleanEmptyModifiers()
        {
            foreach (var modifier in Modifiers.Where(x => x.IsEmpty()).ToList())
            {
                modifier.Reset();
                Modifiers.Remove(modifier);
            }
        }

        /// <summary>
        /// Force reset baseline of bones affected by dynamicbones
        /// to avoid overwriting dynamicbone animations
        /// </summary>
        private static void HandleDynamicBoneModifiers(BoneModifier modifier)
        {
            // Skip non-body modifiers to speed up the check and avoid affecting accessories
            if (modifier.BoneLocation > BoneLocation.BodyTop) return;

            var boneName = modifier.BoneName;
#if KK || KKS || EC
            if (boneName.StartsWith("cf_d_sk_", StringComparison.Ordinal) ||
                boneName.StartsWith("cf_j_bust0", StringComparison.Ordinal) ||
                boneName.StartsWith("cf_d_siri01_", StringComparison.Ordinal) ||
                boneName.StartsWith("cf_j_siri_", StringComparison.Ordinal))
#elif AI || HS2
            if (boneName.StartsWith("cf_J_SiriDam", StringComparison.Ordinal) ||
                boneName.StartsWith("cf_J_Mune00", StringComparison.Ordinal))
#else
                todo fix
#endif
            {
                modifier.Reset();
                modifier.CollectBaseline();
            }
        }
    }

    internal class BoneFinder
    {
        public Dictionary<string, GameObject> CreateBoneDic(GameObject rootObject)
        {
            KKABMX_Core.Logger.LogDebug($"Creating bone dictionary for char={_ctrl.name} rootObj={rootObject}");
            var d = new Dictionary<string, GameObject>();
            FindAll(rootObject.transform, d, _ctrl.objAccessory.Where(x => x != null).Select(x => x.transform).ToArray());
            return d;
        }

        private static void FindAll(Transform transform, Dictionary<string, GameObject> dictObjName, Transform[] excludeTransforms)
        {
            if (!dictObjName.ContainsKey(transform.name))
                dictObjName[transform.name] = transform.gameObject;

            for (var i = 0; i < transform.childCount; i++)
            {
                var childTransform = transform.GetChild(i);
                // Exclude accessories
                if (Array.IndexOf(excludeTransforms, childTransform) < 0)
                    FindAll(childTransform, dictObjName, excludeTransforms);
            }
        }

        private void PurgeDestroyed()
        {
            foreach (var nullGo in _lookup.Keys.Where(x => x == null).ToList()) _lookup.Remove(nullGo);
        }

        public GameObject FindBone(string name, ref BoneLocation location)
        {
            if (location == BoneLocation.BodyTop)
            {
                return FindBone(name, _ctrl.objTop);
            }

            if (location >= BoneLocation.Accessory)
            {
                var accId = location - BoneLocation.Accessory;
                var rootObj = _ctrl.objAccessory.SafeGet(accId);
                return rootObj != null ? FindBone(name, rootObj) : null;
            }

            // Handle unknown locations by looking everywhere. If the bone is found, update the location
            try
            {
                _noRetry = true;
                var bone = FindBone(name, _ctrl.objTop);
                if (bone != null)
                {
                    location = BoneLocation.BodyTop;
                    return bone;
                }

                for (var index = 0; index < _ctrl.objAccessory.Length; index++)
                {
                    var accObj = _ctrl.objAccessory[index];
                    if (accObj != null)
                    {
                        var accBone = FindBone(name, accObj);
                        if (accBone != null)
                        {
                            location = BoneLocation.Accessory + index;
                            return accBone;
                        }
                    }
                }
                return null;
            }
            finally { _noRetry = false; }
        }

        private GameObject FindBone(string name, GameObject rootObject)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (rootObject == null) throw new ArgumentNullException(nameof(rootObject));

            var recreated = false;
            if (!_lookup.TryGetValue(rootObject, out var boneDic))
            {
                boneDic = CreateBoneDic(rootObject);
                recreated = true;
                _lookup[rootObject] = boneDic;
                PurgeDestroyed();
            }

            boneDic.TryGetValue(name, out var boneObj);
            if (boneObj == null && !recreated && !_noRetry)
            {
                boneDic = CreateBoneDic(rootObject);
                _lookup[rootObject] = boneDic;
                boneDic.TryGetValue(name, out boneObj);
            }

            return boneObj;
        }

        private bool _noRetry = false;


        public BoneFinder(ChaControl ctrl)
        {
            _ctrl = ctrl;
            _lookup = new Dictionary<GameObject, Dictionary<string, GameObject>>();
        }

        private readonly Dictionary<GameObject, Dictionary<string, GameObject>> _lookup;
        private readonly ChaControl _ctrl;

        public void AssignBone(BoneModifier modifier)
        {
            var loc = modifier.BoneLocation;
            var bone = FindBone(modifier.BoneName, ref loc);
            modifier.BoneTransform = bone ? bone.transform : null;
            modifier.BoneLocation = loc;
        }
    }
}
