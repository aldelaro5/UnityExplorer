using UnityExplorer.UI;
#if IL2CPP
using Il2CppInterop.Runtime.Injection;
#endif

namespace UnityExplorer;

public class ExplorerBehaviour : MonoBehaviour
{
    internal static ExplorerBehaviour Instance { get; private set; }

#if IL2CPP
    public ExplorerBehaviour(IntPtr ptr) : base(ptr) { }
#endif

    internal static void Setup()
    {
#if IL2CPP
        ClassInjector.RegisterTypeInIl2Cpp<ExplorerBehaviour>();
#endif

        GameObject obj = new("ExplorerBehaviour");
        DontDestroyOnLoad(obj);
        obj.hideFlags = HideFlags.HideAndDontSave;
        Instance = obj.AddComponent<ExplorerBehaviour>();
    }

    internal void Update()
    {
        ExplorerCore.Update();
    }

    // For editor, to clean up objects

    internal void OnDestroy()
    {
        OnApplicationQuit();
    }

    internal bool quitting;

    internal void OnApplicationQuit()
    {
        if (quitting) return;
        quitting = true;
        if (UIManager.UIRoot)
            TryDestroy(UIManager.UIRoot.transform.root.gameObject);

        TryDestroy((typeof(Universe).Assembly.GetType("UniverseLib.UniversalBehaviour")
            .GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic)
            .GetValue(null, null)
            as Component).gameObject);

        TryDestroy(this.gameObject);
    }

    internal void TryDestroy(GameObject obj)
    {
        try
        {
            if (obj)
            {
                Destroy(obj);
            }
        }
        catch (Exception e)
        {
            ExplorerCore.LogError($"Destroy Error!! {e.Message}");
        }
    }
}
