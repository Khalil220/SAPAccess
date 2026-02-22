// Minimal Unity stubs for compilation without interop DLLs.
// These are replaced by real interop assemblies when BepInEx generates them.

namespace UnityEngine
{
    public class Object
    {
        public static void DontDestroyOnLoad(Object target) { }
        public string name { get; set; } = "";
    }

    public class Component : Object { }

    public class Behaviour : Component { }

    public class MonoBehaviour : Behaviour
    {
        public virtual void Awake() { }
        public virtual void Update() { }
    }

    public class GameObject : Object
    {
        public HideFlags hideFlags { get; set; }

        public GameObject() { }
        public GameObject(string name) { this.name = name; }

        public T AddComponent<T>() where T : Component, new() => new T();
    }

    public enum HideFlags
    {
        None = 0,
        HideInHierarchy = 1,
        HideInInspector = 2,
        DontSaveInEditor = 4,
        NotEditable = 8,
        DontSaveInBuild = 16,
        DontUnloadUnusedAsset = 32,
        DontSave = 52,
        HideAndDontSave = 61
    }

    public static class Time
    {
        public static float time => 0f;
    }
}

namespace UnityEngine.InputSystem
{
    public class Keyboard
    {
        public static Keyboard? current => null;
        public KeyControl this[Key key] => new KeyControl();
    }

    public class KeyControl
    {
        public bool wasPressedThisFrame => false;
    }

    public enum Key
    {
        None = 0,
        Space, Enter, Tab, Escape,
        LeftArrow, RightArrow, UpArrow, DownArrow,
        A, B, C, D, E, F, G, H, I, J, K, L, M,
        N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
        F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12
    }
}
