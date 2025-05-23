﻿using HarmonyLib;
using UniverseLib.UI;
using UniverseLib.UI.Models;
#if IL2CPP
using IL2CPPUtils = Il2CppInterop.Common.Il2CppInteropUtils;
#endif

#nullable enable

namespace UnityExplorer.UI.Widgets;

internal class TimeScaleWidget
{
    public static TimeScaleWidget? Instance;

    public static void SetUp(GameObject parent)
    {
        if (Instance == null)
        {
            Instance = new TimeScaleWidget(parent);
        }
    }

    private TimeScaleWidget(GameObject parent)
    {
        Text timeLabel = UIFactory.CreateLabel(parent, "TimeLabel", "Time:", TextAnchor.MiddleRight, Color.grey);
        UIFactory.SetLayoutElement(timeLabel.gameObject, minHeight: 25, minWidth: 35);

        timeInput = UIFactory.CreateInputField(parent, "TimeInput", "timeScale");
        UIFactory.SetLayoutElement(timeInput.Component.gameObject, minHeight: 25, minWidth: 40);
        timeInput.Component.GetOnEndEdit().AddListener(OnTimeInputEndEdit);

        timeInput.Text = string.Empty;
        timeInput.Text = Time.timeScale.ToString();

        lockBtn = UIFactory.CreateButton(parent, "PauseButton", "Lock", new Color(0.2f, 0.2f, 0.2f));
        UIFactory.SetLayoutElement(lockBtn.Component.gameObject, minHeight: 25, minWidth: 50);
        lockBtn.OnClick += OnPauseButtonClicked;
    }

    public float DesiredTime { get; private set; }

    private readonly InputFieldRef timeInput;
    private readonly ButtonRef lockBtn;

    private bool locked;
    private bool settingTimeScale;

    public void Update()
    {
        // Fallback in case Time.timeScale patch failed for whatever reason
        if (locked)
        {
            UpdateTimeScale();
        }

        if (!timeInput.Component.isFocused)
        {
            timeInput.Text = Time.timeScale.ToString("F2");
        }
    }

    public void LockTo(float timeScale)
    {
        locked = true;
        SetTimeScale(timeScale);
        UpdateUi();
    }

    public void OnPauseButtonClicked()
    {
        OnTimeInputEndEdit(timeInput.Text);

        locked = !locked;

        UpdateUi();
    }

    void UpdateTimeScale()
    {
        settingTimeScale = true;
        Time.timeScale = DesiredTime;
        settingTimeScale = false;
    }

    void SetTimeScale(float floatVal)
    {
        DesiredTime = floatVal;
        UpdateTimeScale();
    }

    // UI event listeners

    void OnTimeInputEndEdit(string val)
    {
        if (float.TryParse(val, out float f))
        {
            SetTimeScale(f);
        }
    }

    void UpdateUi()
    {
        Color color = locked ? new Color(0.3f, 0.3f, 0.2f) : new Color(0.2f, 0.2f, 0.2f);
        RuntimeHelper.SetColorBlock(lockBtn.Component, color, color * 1.2f, color * 0.7f);
        lockBtn.ButtonText.text = locked ? "Unlock" : "Lock";
    }

    // Only allow Time.timeScale to be set if the user hasn't "locked" it or if we are setting the value internally.

    static void InitPatch()
    {

        try
        {
            var target = typeof(Time).GetProperty("timeScale")?.GetSetMethod();
            if (target == null)
            {
                return;
            }
#if IL2CPP
            var fieldInfo = IL2CPPUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(target);
            if (fieldInfo == null)
            {
                return;
            }
#endif
            ExplorerCore.Harmony.Patch(target,
                prefix: new(AccessTools.Method(typeof(TimeScaleWidget), nameof(Prefix_Time_set_timeScale))));
        }
        catch (Exception e)
        {
            ExplorerCore.LogError($"Failed to patch Time.timeScale setter, {e.Message}");
        }
    }

    static bool Prefix_Time_set_timeScale()
        => Instance == null || !Instance.locked || Instance.settingTimeScale;
}
