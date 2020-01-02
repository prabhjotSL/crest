﻿// Crest Ocean System

// This file is subject to the MIT License as seen in the root of this folder structure (LICENSE)

using UnityEngine;

namespace Crest
{
    /// <summary>
    /// This script and associated shader approximate the interaction between a sphere and the water. Multiple
    /// spheres can be used to model the interaction of a non-spherical shape.
    /// </summary>
    public class SphereWaterInteraction : MonoBehaviour
    {
        float Radius => 0.5f * transform.lossyScale.x;

        [Range(-1f, 1f), SerializeField]
        float _weight = 1f;
        [Range(0f, 2f), SerializeField]
        float _weightUpDownMul = 0.5f;

        [Header("Noise")]
        [Range(0f, 1f), SerializeField]
        float _noiseAmp = 0.5f;

        [Range(0f, 10f), SerializeField]
        float _noiseFreq = 6f;

        [Header("Limits")]
        [Tooltip("Teleport speed (km/h) - if the calculated speed is larger than this amount, the object is deemed to have teleported and the computed velocity is discarded."), SerializeField]
        float _teleportSpeed = 500f;
        [SerializeField]
        bool _warnOnTeleport = false;
        [Tooltip("Maximum speed clamp (km/h), useful for controlling/limiting wake."), SerializeField]
        float _maxSpeed = 100f;
        [SerializeField]
        bool _warnOnSpeedClamp = false;

        RegisterDynWavesInput _dynWavesInput;
        FloatingObjectBase _boat;

        Vector3 _localPositionRest;
        Vector3 _posLast;

        SampleFlowHelper _sampleFlowHelper = new SampleFlowHelper();

        Renderer _renderer;
        MaterialPropertyBlock _mpb;

        readonly static int sp_velocity = Shader.PropertyToID("_Velocity");
        readonly static int sp_weight = Shader.PropertyToID("_Weight");
        readonly static int sp_simDeltaTime = Shader.PropertyToID("_SimDeltaTime");
        readonly static int sp_radius = Shader.PropertyToID("_Radius");

        private void Start()
        {
            if (OceanRenderer.Instance == null || !OceanRenderer.Instance.CreateDynamicWaveSim)
            {
                enabled = false;
                return;
            }

            if (transform.parent == null)
            {
                Debug.LogError("ObjectWaterInteraction script requires a parent GameObject.", this);
                enabled = false;
                return;
            }

            _localPositionRest = transform.localPosition;

            _dynWavesInput = GetComponent<RegisterDynWavesInput>();
            if (_dynWavesInput == null)
            {
                Debug.LogError("ObjectWaterInteraction script requires RegisterDynWavesInput script to be present.", this);
                enabled = false;
                return;
            }

            _boat = GetComponentInParent<FloatingObjectBase>();
            if (_boat == null)
            {
                _boat = transform.parent.gameObject.AddComponent<ObjectWaterInteractionAdaptor>();
            }

            _renderer = GetComponent<Renderer>();
            if (_renderer == null)
            {
                Debug.LogError("ObjectWaterInteraction script requires Renderer component.", this);
                enabled = false;
                return;
            }

            _mpb = new MaterialPropertyBlock();
        }

        void LateUpdate()
        {
            var ocean = OceanRenderer.Instance;
            if (ocean == null) return;

            // Which lod is this object in (roughly)?
            int simsActive;
            if (!LateUpdateComputeOverlappingSimCount(out simsActive, out int simsPresent))
            {
                if (simsPresent == 0)
                {
                    // Counting non-existent sims is expensive - stop updating if none found
                    enabled = false;
                }

                // No sims running - abort. don't bother switching off renderer - camera wont be active
                return;
            }

            var disp = _boat.CalculateDisplacementToObject();

            // Set position of interaction
            {
                var dispFlatLand = disp;
                dispFlatLand.y = 0f;
                var velBoat = _boat.Velocity;
                velBoat.y = 0f;
                transform.position = transform.parent.TransformPoint(_localPositionRest) - dispFlatLand;
                transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            }

            // Velocity relative to water
            Vector3 relativeVelocity = LateUpdateComputeVelRelativeToWater(ocean);

            float dt; int steps;
            ocean._lodDataDynWaves.GetSimSubstepData(ocean.DeltaTimeDynamics, out steps, out dt);

            float weight = _weight / simsActive;

            var waterHeight = disp.y + ocean.SeaLevel;
            LateUpdateSphereWeight(waterHeight, ref weight);

            _renderer.GetPropertyBlock(_mpb);

            _mpb.SetVector(sp_velocity, relativeVelocity);
            _mpb.SetFloat(sp_weight, weight);
            _mpb.SetFloat(sp_simDeltaTime, dt);
            _mpb.SetFloat(sp_radius, Radius);

            _renderer.SetPropertyBlock(_mpb);

            _posLast = transform.position;
        }

        // Multiple sims run at different scales in the world. Count how many sims this interaction will overlap, so that
        // we can normalize the interaction force for the number of sims.
        bool LateUpdateComputeOverlappingSimCount(out int simsActive, out int simsPresent)
        {
            simsActive = 0;
            simsPresent = 0;

            var thisRect = new Rect(new Vector2(transform.position.x, transform.position.z), Vector3.zero);
            var minLod = LodDataMgrAnimWaves.SuggestDataLOD(thisRect);
            if (minLod == -1)
            {
                // Outside all lods, nothing to update!
                return false;
            }

            // How many active wave sims currently apply to this object - ideally this would eliminate sims that are too
            // low res, by providing a max grid size param
            LodDataMgrDynWaves.CountWaveSims(minLod, out simsPresent, out simsActive);

            if (simsPresent == 0)
            {
                return false;
            }

            // No sims running - abort
            return simsActive > 0;
        }

        // Velocity of the sphere, relative to the water. Computes on the fly, discards if teleport detected.
        Vector3 LateUpdateComputeVelRelativeToWater(OceanRenderer ocean)
        {
            Vector3 vel;

            var rnd = 1f + _noiseAmp * (2f * Mathf.PerlinNoise(_noiseFreq * ocean.CurrentTime, 0.5f) - 1f);
            // feed in water velocity
            vel = (transform.position - _posLast) / ocean.DeltaTimeDynamics;
            if (ocean.DeltaTimeDynamics < 0.0001f)
            {
                vel = Vector3.zero;
            }

            if (QueryFlow.Instance)
            {
                _sampleFlowHelper.Init(transform.position, _boat.ObjectWidth);
                Vector2 surfaceFlow = Vector2.zero;
                _sampleFlowHelper.Sample(ref surfaceFlow);
                vel -= new Vector3(surfaceFlow.x, 0, surfaceFlow.y);
            }
            vel.y *= _weightUpDownMul;

            var speedKmh = vel.magnitude * 3.6f;
            if (speedKmh > _teleportSpeed)
            {
                // teleport detected
                vel *= 0f;

                if (_warnOnTeleport)
                {
                    Debug.LogWarning("Teleport detected (speed = " + speedKmh.ToString() + "), velocity discarded.", this);
                }
            }
            else if (speedKmh > _maxSpeed)
            {
                // limit speed to max
                vel *= _maxSpeed / speedKmh;

                if (_warnOnSpeedClamp)
                {
                    Debug.LogWarning("Speed (" + speedKmh.ToString() + ") exceeded max limited, clamped.", this);
                }
            }

            return vel;
        }

        // Weight based on submerged-amount of sphere
        void LateUpdateSphereWeight(float waterHeight, ref float weight)
        {
            var centerDepthInWater = waterHeight - transform.position.y;

            if (centerDepthInWater >= 0f)
            {
                // Center in water - exponential fall off of interaction influence as object gets deeper
                var prop = centerDepthInWater / Radius;
                prop *= 0.5f;
                weight *= Mathf.Exp(-prop * prop);
            }
            else
            {
                // Center out of water - ramp off with square root, weight goes to 0 when sphere is just touching water
                var height = -centerDepthInWater;
                var heightProp = 1f - Mathf.Clamp01(height / Radius);
                weight *= Mathf.Sqrt(heightProp);
            }
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0f, 1f, 0f, 0.5f);
            Gizmos.DrawWireSphere(transform.position, Radius);
        }
    }
}