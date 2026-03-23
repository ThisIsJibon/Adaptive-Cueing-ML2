using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

[RequireComponent(typeof(ARRaycastManager))]
public class GroundCueSpawner : MonoBehaviour
{
    [Header("Settings")]
    [Tooltip("The Cube or Tile prefab to spawn on the ground.")]
    public GameObject cubePrefab;

    [Header("Input Settings")]
    [Tooltip("The name of the control to listen for (Trigger on ML2).")]
    public string triggerControlName = "trigger";

    private ARRaycastManager raycastManager;
    private static List<ARRaycastHit> s_Hits = new List<ARRaycastHit>();

    void Awake()
    {
        // Automatically get the Raycast Manager attached to the XR Rig
        raycastManager = GetComponent<ARRaycastManager>();
    }

    void Update()
    {
        // 1. Find the XR Controller (Magic Leap 2 Controller)
        // We use the base XRController class to avoid namespace errors
        var controller = InputSystem.GetDevice<UnityEngine.InputSystem.XR.XRController>();

        if (controller != null)
        {
            // 2. Access the trigger button specifically
            var trigger = controller[triggerControlName] as ButtonControl;

            // 3. If trigger was pressed this frame, attempt to spawn
            if (trigger != null && trigger.wasPressedThisFrame)
            {
                SpawnCubeAtGaze();
            }
        }
    }

    void SpawnCubeAtGaze()
    {
        // 4. Center of the Magic Leap 2 view (Screen space)
        Vector2 screenCenter = new Vector2(Screen.width / 2, Screen.height / 2);

        // 5. Cast a ray into the real world to find 'Planes' (the floor)
        // TrackableType.PlaneWithinPolygon ensures we only hit detected flat surfaces
        if (raycastManager.Raycast(screenCenter, s_Hits, TrackableType.PlaneWithinPolygon))
        {
            // Raycast hits are sorted by distance; index 0 is the closest surface
            Pose hitPose = s_Hits[0].pose;

            // 6. Instantiate the cube at the hit location
            // We use hitPose.rotation so the cube aligns with the floor's slope
            Instantiate(cubePrefab, hitPose.position, hitPose.rotation);
            
            Debug.Log($"Cue deployed at: {hitPose.position}");
        }
        else
        {
            Debug.Log("No ground detected at gaze point. Try scanning the floor more.");
        }
    }
}