using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.InputSystem;
using System.Collections.Generic;

[RequireComponent(typeof(XRGrabInteractable))]
public class PointCloudController : MonoBehaviour
{
    [Header("Scaling Settings")]
    public float minScale = 0.05f;
    public float maxScale = 50.0f;
    public float scaleSpeed = 1.5f;

    [Header("Rotation Settings")]
    public float rotationSpeed = 90.0f; // degrees per second

    [Header("PC Controls Settings")]
    public float pcMoveSpeed = 4.0f;
    public float pcRotateSpeed = 100.0f;
    public float pcScaleSpeed = 2.0f;

    [HideInInspector]
    public bool isControlEnabled = true;

    private XRGrabInteractable grabInteractable;
    private Vector3 initialScale;

    void Start()
    {
        grabInteractable = GetComponent<XRGrabInteractable>();
        initialScale = transform.localScale;
        
        // Ensure Rigidbody is present for XRI Grab
        Rigidbody rb = GetComponent<Rigidbody>();
        if (rb == null)
        {
            rb = gameObject.AddComponent<Rigidbody>();
        }
        rb.isKinematic = true;
        rb.useGravity = false;

        // Auto-assign setup for Collider if missing (needed for grab detection)
        Collider col = GetComponent<Collider>();
        if (col == null)
        {
            BoxCollider box = gameObject.AddComponent<BoxCollider>();
            box.center = new Vector3(0, 2f, 0); 
            box.size = new Vector3(10f, 4f, 10f); 
            box.isTrigger = true; 
        }

        // Adjust Grab Interactable settings for smoother VR handling
        grabInteractable.trackPosition = true;
        grabInteractable.trackRotation = true;
        grabInteractable.throwOnDetach = false;
    }

    void Update()
    {
        // 1. VR Controls (when grabbed by VR Controllers)
        if (grabInteractable != null && grabInteractable.isSelected)
        {
            foreach (var interactor in grabInteractable.interactorsSelecting)
            {
                XRNode handNode = XRNode.RightHand; // default fallback
                string interactorName = interactor.transform.gameObject.name.ToLower();
                if (interactorName.Contains("left"))
                {
                    handNode = XRNode.LeftHand;
                }
                else if (interactorName.Contains("right"))
                {
                    handNode = XRNode.RightHand;
                }

                ProcessControllerInput(handNode);
            }
        }

        // 2. PC Keyboard & Mouse Controls (always active for testing in Editor)
        ProcessPCInput();
    }

    private void ProcessPCInput()
    {
        if (!isControlEnabled) return;

        float h = 0f;
        float v = 0f;
        float verticalMove = 0f;
        float rotateX = 0f;
        float rotateY = 0f;
        float scroll = 0f;

        bool newInputSupported = false;
        try
        {
            // 1. Try New Input System
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                newInputSupported = true;
                // Inverted key directions to simulate camera movement (W/S moves forward/back, A/D moves left/right)
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) v = -1.0f; // Inverted
                if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) v = 1.0f;  // Inverted
                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) h = -1.0f; // Inverted
                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) h = 1.0f;  // Inverted

                if (keyboard.eKey.isPressed) verticalMove = -1.0f; // Inverted (E = camera up -> object down)
                if (keyboard.qKey.isPressed) verticalMove = 1.0f;  // Inverted (Q = camera down -> object up)
            }

            var mouse = Mouse.current;
            if (mouse != null)
            {
                newInputSupported = true;
                
                    Vector2 mouseDelta = mouse.delta.ReadValue();
                    if (mouse.leftButton.isPressed)
                    {
                        // Left Mouse -> Rotate object (matches camera Left = Rotate)
                        rotateX = -mouseDelta.x * 0.1f;
                        rotateY = -mouseDelta.y * 0.1f;
                    }
                    else if (mouse.rightButton.isPressed)
                    {
                        // Right Mouse -> Translate object (matches camera Right = Pan)
                        h = -mouseDelta.x * 0.02f;
                        verticalMove = mouseDelta.y * 0.02f;
                    }

                Vector2 scrollDelta = mouse.scroll.ReadValue();
                if (Mathf.Abs(scrollDelta.y) > 0.01f)
                {
                    scroll = Mathf.Sign(scrollDelta.y);
                }
            }
        }
        catch (System.Exception)
        {
            newInputSupported = false;
        }

        // 2. Fallback to Legacy Input System if new one didn't provide active input or is not supported/configured
        if (!newInputSupported)
        {
            // Legacy Keyboard Input (Inverted to simulate camera movement)
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) v = -1.0f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) v = 1.0f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) h = -1.0f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) h = 1.0f;

            if (Input.GetKey(KeyCode.E)) verticalMove = -1.0f;
            if (Input.GetKey(KeyCode.Q)) verticalMove = 1.0f;

            if (Input.GetMouseButton(0))
            {
                // Left Mouse -> Rotate object
                rotateX = -Input.GetAxis("Mouse X") * 0.5f;
                rotateY = -Input.GetAxis("Mouse Y") * 0.5f;
            }
            else if (Input.GetMouseButton(1))
            {
                // Right Mouse -> Translate object (pan)
                h = -Input.GetAxis("Mouse X") * 0.1f;
                verticalMove = Input.GetAxis("Mouse Y") * 0.1f;
            }

            // Legacy Scroll Input
            float legacyScroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(legacyScroll) > 0.01f)
            {
                scroll = Mathf.Sign(legacyScroll);
            }
        }

        // Apply movement
        if (Mathf.Abs(h) > 0.01f || Mathf.Abs(v) > 0.01f)
        {
            Vector3 moveDir = new Vector3(h, 0, v).normalized;
            transform.Translate(moveDir * pcMoveSpeed * Time.deltaTime, Space.World);
        }

        if (Mathf.Abs(verticalMove) > 0.01f)
        {
            transform.Translate(Vector3.up * verticalMove * pcMoveSpeed * Time.deltaTime, Space.World);
        }

        // Apply rotation
        if (Mathf.Abs(rotateX) > 0.01f)
        {
            transform.Rotate(Vector3.up, rotateX * pcRotateSpeed * Time.deltaTime, Space.World);
        }
        if (Mathf.Abs(rotateY) > 0.01f)
        {
            transform.Rotate(Vector3.right, rotateY * pcRotateSpeed * Time.deltaTime, Space.Self);
        }

        // Apply scaling
        if (Mathf.Abs(scroll) > 0.01f)
        {
            float scaleFactor = 1.0f + (scroll * 0.1f * pcScaleSpeed);
            Vector3 newScale = transform.localScale * scaleFactor;

            newScale.x = Mathf.Clamp(newScale.x, minScale, maxScale);
            newScale.y = Mathf.Clamp(newScale.y, minScale, maxScale);
            newScale.z = Mathf.Clamp(newScale.z, minScale, maxScale);

            transform.localScale = newScale;
        }
    }

    private void ProcessControllerInput(XRNode handNode)
    {
        var devices = new List<UnityEngine.XR.InputDevice>();
        UnityEngine.XR.InputDevices.GetDevicesAtXRNode(handNode, devices);

        if (devices.Count > 0)
        {
            UnityEngine.XR.InputDevice device = devices[0];
            
            // Read 2D Joystick/Thumbstick input
            Vector2 stickValue;
            if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out stickValue))
            {
                // 1. Scale object using Joystick Vertical Axis (Y)
                if (Mathf.Abs(stickValue.y) > 0.1f)
                {
                    float scaleFactor = 1.0f + (stickValue.y * scaleSpeed * Time.deltaTime);
                    Vector3 newScale = transform.localScale * scaleFactor;
                    
                    // Clamp scale to safety limits
                    newScale.x = Mathf.Clamp(newScale.x, minScale, maxScale);
                    newScale.y = Mathf.Clamp(newScale.y, minScale, maxScale);
                    newScale.z = Mathf.Clamp(newScale.z, minScale, maxScale);
                    
                    transform.localScale = newScale;
                }

                // 2. Rotate object around Y axis using Joystick Horizontal Axis (X)
                if (Mathf.Abs(stickValue.x) > 0.1f)
                {
                    float rotationAmount = stickValue.x * rotationSpeed * Time.deltaTime;
                    transform.Rotate(Vector3.up, -rotationAmount, Space.Self);
                }
            }
        }
    }

    // Public reset method
    public void ResetTransform()
    {
        transform.localScale = initialScale;
        transform.localPosition = Vector3.zero;
        transform.localRotation = Quaternion.identity;
    }

    // Reset initial scale manually when calibration is loaded
    public void ResetInitialScale(Vector3 scale)
    {
        initialScale = scale;
    }
}
