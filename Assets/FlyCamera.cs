using UnityEngine;
using UnityEngine.InputSystem;

// Desktop camera: hold the right mouse button to look around, WASD to move, Q/E down/up,
// Shift to go faster, mouse wheel to change the speed.
public class FlyCamera : MonoBehaviour
{
    public float lookSensitivity = 0.15f;   // degrees per pixel of mouse movement
    public float moveSpeed = 20f;           // m/s
    public float fastMultiplier = 4f;

    float yaw, pitch;

    void Start()
    {
        Vector3 e = transform.eulerAngles;
        yaw = e.y;
        pitch = e.x > 180 ? e.x - 360 : e.x;
    }

    void Update()
    {
        var mouse = Mouse.current; var kb = Keyboard.current;
        if (mouse == null || kb == null) return;

        bool looking = mouse.rightButton.isPressed;
        Cursor.lockState = looking ? CursorLockMode.Locked : CursorLockMode.None;
        if (looking)
        {
            Vector2 d = mouse.delta.ReadValue();
            yaw += d.x * lookSensitivity;
            pitch = Mathf.Clamp(pitch - d.y * lookSensitivity, -89f, 89f);
            transform.rotation = Quaternion.Euler(pitch, yaw, 0);
        }

        float scroll = mouse.scroll.ReadValue().y;
        if (scroll != 0) moveSpeed = Mathf.Clamp(moveSpeed * (scroll > 0 ? 1.2f : 1 / 1.2f), 1f, 500f);

        var v = new Vector3(
            (kb.dKey.isPressed ? 1 : 0) - (kb.aKey.isPressed ? 1 : 0),
            (kb.eKey.isPressed ? 1 : 0) - (kb.qKey.isPressed ? 1 : 0),
            (kb.wKey.isPressed ? 1 : 0) - (kb.sKey.isPressed ? 1 : 0));
        if (v != Vector3.zero)
        {
            float speed = moveSpeed * (kb.leftShiftKey.isPressed ? fastMultiplier : 1f);
            transform.position += transform.TransformDirection(v.normalized) * speed * Time.unscaledDeltaTime;
        }
    }
}
