using HarmonyLib;
using UniverseLib.Input;
using UniverseLib.UI;
using UniverseLib.UI.Models;
#if IL2CPP
using Il2CppInterop.Runtime.Injection;
#endif

namespace UnityExplorer.UI.Panels
{
    internal class FreeCamPanel : UEPanel
    {
        public FreeCamPanel(UIBase owner) : base(owner)
        {
        }

        public override string Name => "Freecam";
        public override UIManager.Panels PanelType => UIManager.Panels.Freecam;
        public override int MinWidth => 410;
        public override int MinHeight => 310;
        public override Vector2 DefaultAnchorMin => new(0.4f, 0.4f);
        public override Vector2 DefaultAnchorMax => new(0.6f, 0.6f);
        public override bool NavButtonWanted => true;
        public override bool ShouldSaveActiveState => true;

        internal static bool inFreeCamMode;
        internal static bool usingGameCamera;
        internal static Camera ourCamera;
        internal static Camera lastMainCamera;
        internal static FreeCamBehaviour freeCamScript;

        internal static float desiredMoveSpeed = 10f;

        internal static Vector3 originalCameraPosition;
        internal static Quaternion originalCameraRotation;

        internal static Vector3? currentUserCameraPosition;
        internal static Quaternion? currentUserCameraRotation;

        internal static Vector3 previousMousePosition;

        internal static Vector3 lastSetCameraPosition;

        static ButtonRef startStopButton;
        static Toggle useGameCameraToggle;
        static Toggle lockCameraToggle;
        static Text lockCameraText;
        static InputFieldRef positionInput;
        static InputFieldRef moveSpeedInput;
        static ButtonRef inspectButton;

        internal static void BeginFreecam()
        {
            inFreeCamMode = true;

            previousMousePosition = InputManager.MousePosition;

            CacheMainCamera();
            SetupFreeCamera();

            inspectButton.GameObject.SetActive(true);
        }

        static void CacheMainCamera()
        {
            Camera currentMain = Camera.main;
            if (currentMain)
            {
                lastMainCamera = currentMain;
                originalCameraPosition = currentMain.transform.position;
                originalCameraRotation = currentMain.transform.rotation;

                if (currentUserCameraPosition == null)
                {
                    currentUserCameraPosition = currentMain.transform.position;
                    currentUserCameraRotation = currentMain.transform.rotation;
                }
            }
            else
                originalCameraRotation = Quaternion.identity;
        }

        static void SetupFreeCamera()
        {
            if (useGameCameraToggle.isOn)
            {
                if (!lastMainCamera)
                {
                    ExplorerCore.LogWarning($"There is no previous Camera found, reverting to default Free Cam.");
                    useGameCameraToggle.isOn = false;
                }
                else
                {
                    usingGameCamera = true;
                    ourCamera = lastMainCamera;
                }
            }

            if (!useGameCameraToggle.isOn)
            {
                usingGameCamera = false;

                if (lastMainCamera)
                    lastMainCamera.enabled = false;
            }

            if (!ourCamera)
            {
                ourCamera = new GameObject("UE_Freecam").AddComponent<Camera>();
                ourCamera.gameObject.tag = "MainCamera";
                GameObject.DontDestroyOnLoad(ourCamera.gameObject);
                ourCamera.gameObject.hideFlags = HideFlags.HideAndDontSave;
            }

            if (!freeCamScript)
                freeCamScript = ourCamera.gameObject.AddComponent<FreeCamBehaviour>();

            ourCamera.transform.position = (Vector3)currentUserCameraPosition;
            ourCamera.transform.rotation = (Quaternion)currentUserCameraRotation;

            ourCamera.gameObject.SetActive(true);
            ourCamera.enabled = true;
        }

        internal static void EndFreecam()
        {
            inFreeCamMode = false;

            if (usingGameCamera)
            {
                ourCamera = null;

                if (lastMainCamera)
                {
                    lastMainCamera.transform.position = originalCameraPosition;
                    lastMainCamera.transform.rotation = originalCameraRotation;
                }
            }

            if (ourCamera)
                ourCamera.gameObject.SetActive(false);
            else
                inspectButton.GameObject.SetActive(false);

            if (freeCamScript)
            {
                GameObject.Destroy(freeCamScript);
                freeCamScript = null;
            }

            if (lastMainCamera)
                lastMainCamera.enabled = true;
        }

        static void SetCameraPosition(Vector3 pos)
        {
            if (!ourCamera || lastSetCameraPosition == pos)
                return;

            ourCamera.transform.position = pos;
            lastSetCameraPosition = pos;
        }

        internal static void UpdatePositionInput()
        {
            if (!ourCamera)
                return;

            if (positionInput.Component.isFocused)
                return;

            lastSetCameraPosition = ourCamera.transform.position;
            positionInput.Text = ParseUtility.ToStringForInput<Vector3>(lastSetCameraPosition);
        }

        // ~~~~~~~~ UI construction / callbacks ~~~~~~~~

        protected override void ConstructPanelContent()
        {
            startStopButton = UIFactory.CreateButton(ContentRoot, "ToggleButton", "Freecam");
            UIFactory.SetLayoutElement(startStopButton.GameObject, minWidth: 150, minHeight: 25, flexibleWidth: 9999);
            startStopButton.OnClick += StartStopButton_OnClick;
            SetToggleButtonState();

            AddSpacer(5);

            GameObject toggleRowObj = UIFactory.CreateHorizontalGroup(this.ContentRoot, "ToggleRow", false, false, true, true, 5, default, new(0.07f, 0.07f, 0.07f), TextAnchor.MiddleLeft);
            UIFactory.SetLayoutElement(toggleRowObj, minHeight: 25, flexibleHeight: 0, flexibleWidth: 9999);

            GameObject toggleLockCameraObj = UIFactory.CreateToggle(toggleRowObj, "LockCamera", out lockCameraToggle, out lockCameraText);
            UIFactory.SetLayoutElement(toggleLockCameraObj, minHeight: 25, flexibleWidth: 9999);
            lockCameraToggle.onValueChanged.AddListener(OnLockCameraToggle);
            lockCameraToggle.isOn = false;
            lockCameraText.text = "Lock Camera With Game Control";
            lockCameraText.color = Color.gray;

            GameObject toggleGameCameraObj = UIFactory.CreateToggle(toggleRowObj, "UseGameCameraToggle", out useGameCameraToggle, out Text toggleGameCameraText);
            UIFactory.SetLayoutElement(toggleGameCameraObj, minHeight: 25, flexibleWidth: 9999);
            useGameCameraToggle.onValueChanged.AddListener(OnUseGameCameraToggled);
            useGameCameraToggle.isOn = false;
            toggleGameCameraText.text = "Use Game Camera?";

            AddSpacer(5);

            GameObject posRow = AddInputField("Position", "Freecam Pos:", "eg. 0 0 0", out positionInput, PositionInput_OnEndEdit);

            ButtonRef resetPosButton = UIFactory.CreateButton(posRow, "ResetButton", "Reset");
            UIFactory.SetLayoutElement(resetPosButton.GameObject, minWidth: 70, minHeight: 25);
            resetPosButton.OnClick += OnResetPosButtonClicked;

            AddSpacer(5);

            AddInputField("MoveSpeed", "Move Speed:", "Default: 1", out moveSpeedInput, MoveSpeedInput_OnEndEdit);
            moveSpeedInput.Text = desiredMoveSpeed.ToString();

            AddSpacer(5);

            string instructions = @"Controls:
- WASD / Arrows: Movement
- E / PgUp: Move up
- Q / PgDown: Move down
- Right Mouse Button: Free look
- Shift: Super speed";

            Text instructionsText = UIFactory.CreateLabel(ContentRoot, "Instructions", instructions, TextAnchor.UpperLeft);
            UIFactory.SetLayoutElement(instructionsText.gameObject, flexibleWidth: 9999, flexibleHeight: 9999);

            AddSpacer(5);

            inspectButton = UIFactory.CreateButton(ContentRoot, "InspectButton", "Inspect Free Camera");
            UIFactory.SetLayoutElement(inspectButton.GameObject, flexibleWidth: 9999, minHeight: 25);
            inspectButton.OnClick += () => { InspectorManager.Inspect(ourCamera); };
            inspectButton.GameObject.SetActive(false);

            AddSpacer(5);

            ExplorerCore.Harmony.PatchAll(typeof(FreeCamBehaviour));
        }

        protected override void LateConstructUI()
        {
            base.LateConstructUI();
            lockCameraToggle.enabled = false;
        }

        private void OnLockCameraToggle(bool value)
        {
            if (freeCamScript != null)
                freeCamScript.enabled = !value;
        }

        void AddSpacer(int height)
        {
            GameObject obj = UIFactory.CreateUIObject("Spacer", ContentRoot);
            UIFactory.SetLayoutElement(obj, minHeight: height, flexibleHeight: 0);
        }

        GameObject AddInputField(string name, string labelText, string placeHolder, out InputFieldRef inputField, Action<string> onInputEndEdit)
        {
            GameObject row = UIFactory.CreateHorizontalGroup(ContentRoot, $"{name}_Group", false, false, true, true, 3, default, new(1, 1, 1, 0));

            Text posLabel = UIFactory.CreateLabel(row, $"{name}_Label", labelText);
            UIFactory.SetLayoutElement(posLabel.gameObject, minWidth: 100, minHeight: 25);

            inputField = UIFactory.CreateInputField(row, $"{name}_Input", placeHolder);
            UIFactory.SetLayoutElement(inputField.GameObject, minWidth: 125, minHeight: 25, flexibleWidth: 9999);
            inputField.Component.GetOnEndEdit().AddListener(onInputEndEdit);

            return row;
        }

        void StartStopButton_OnClick()
        {
            EventSystemHelper.SetSelectedGameObject(null);

            if (inFreeCamMode)
                EndFreecam();
            else
                BeginFreecam();

            SetToggleButtonState();
        }

        void SetToggleButtonState()
        {
            if (inFreeCamMode)
            {
                RuntimeHelper.SetColorBlockAuto(startStopButton.Component, new(0.4f, 0.2f, 0.2f));
                startStopButton.ButtonText.text = "End Freecam";
            }
            else
            {
                RuntimeHelper.SetColorBlockAuto(startStopButton.Component, new(0.2f, 0.4f, 0.2f));
                startStopButton.ButtonText.text = "Begin Freecam";
            }

            if (lockCameraToggle != null)
            {
                lockCameraToggle.enabled = inFreeCamMode;
                lockCameraText.color = lockCameraToggle.enabled ? Color.white : Color.grey;
                lockCameraToggle.isOn = false;
            }
        }

        void OnUseGameCameraToggled(bool value)
        {
            EventSystemHelper.SetSelectedGameObject(null);

            if (!inFreeCamMode)
                return;

            EndFreecam();
            BeginFreecam();
        }

        void OnResetPosButtonClicked()
        {
            currentUserCameraPosition = originalCameraPosition;
            currentUserCameraRotation = originalCameraRotation;

            if (inFreeCamMode && ourCamera)
            {
                ourCamera.transform.position = (Vector3)currentUserCameraPosition;
                ourCamera.transform.rotation = (Quaternion)currentUserCameraRotation;
            }

            positionInput.Text = ParseUtility.ToStringForInput<Vector3>(originalCameraPosition);
        }

        void PositionInput_OnEndEdit(string input)
        {
            EventSystemHelper.SetSelectedGameObject(null);

            if (!ParseUtility.TryParse(input, out Vector3 parsed, out Exception parseEx))
            {
                ExplorerCore.LogWarning($"Could not parse position to Vector3: {parseEx.ReflectionExToString()}");
                UpdatePositionInput();
                return;
            }

            SetCameraPosition(parsed);
        }

        void MoveSpeedInput_OnEndEdit(string input)
        {
            EventSystemHelper.SetSelectedGameObject(null);

            if (!ParseUtility.TryParse(input, out float parsed, out Exception parseEx))
            {
                ExplorerCore.LogWarning($"Could not parse value: {parseEx.ReflectionExToString()}");
                moveSpeedInput.Text = desiredMoveSpeed.ToString();
                return;
            }

            desiredMoveSpeed = parsed;
        }
    }

    internal class FreeCamBehaviour : MonoBehaviour
    {
#if IL2CPP
        static FreeCamBehaviour()
        {
            ClassInjector.RegisterTypeInIl2Cpp<FreeCamBehaviour>();
        }

        public FreeCamBehaviour(IntPtr ptr) : base(ptr) { }
#endif

        private static bool _shouldEatKeys;

        internal void Update()
        {
            _shouldEatKeys = false;

            if (FreeCamPanel.inFreeCamMode)
            {
                if (!FreeCamPanel.ourCamera)
                {
                    FreeCamPanel.EndFreecam();
                    return;
                }

                Transform transform = FreeCamPanel.ourCamera.transform;

                FreeCamPanel.currentUserCameraPosition = transform.position;
                FreeCamPanel.currentUserCameraRotation = transform.rotation;

                float moveSpeed = FreeCamPanel.desiredMoveSpeed * Time.deltaTime;

                if (InputManager.GetKey(KeyCode.LeftShift) || InputManager.GetKey(KeyCode.RightShift))
                    moveSpeed *= 10f;

                if (InputManager.GetKey(KeyCode.LeftArrow) || InputManager.GetKey(KeyCode.A))
                    transform.position += transform.right * -1 * moveSpeed;

                if (InputManager.GetKey(KeyCode.RightArrow) || InputManager.GetKey(KeyCode.D))
                    transform.position += transform.right * moveSpeed;

                if (InputManager.GetKey(KeyCode.UpArrow) || InputManager.GetKey(KeyCode.W))
                    transform.position += transform.forward * moveSpeed;

                if (InputManager.GetKey(KeyCode.DownArrow) || InputManager.GetKey(KeyCode.S))
                    transform.position += transform.forward * -1 * moveSpeed;

                if (InputManager.GetKey(KeyCode.E) || InputManager.GetKey(KeyCode.PageUp))
                    transform.position += transform.up * moveSpeed;

                if (InputManager.GetKey(KeyCode.Q) || InputManager.GetKey(KeyCode.PageDown))
                    transform.position += transform.up * -1 * moveSpeed;

                if (InputManager.GetMouseButton(1))
                {
                    Vector3 mouseDelta = InputManager.MousePosition - FreeCamPanel.previousMousePosition;

                    float newRotationX = transform.localEulerAngles.y + mouseDelta.x * 0.3f;
                    float newRotationY = transform.localEulerAngles.x - mouseDelta.y * 0.3f;
                    transform.localEulerAngles = new Vector3(newRotationY, newRotationX, 0f);
                }

                FreeCamPanel.UpdatePositionInput();

                FreeCamPanel.previousMousePosition = InputManager.MousePosition;

                // We need to not let the game see any inputs other than mouse press so inputs aren't seen while
                // operating the camera
                _shouldEatKeys = true;
            }
        }

        private static bool ShouldEatInput()
        {
            return _shouldEatKeys && FreeCamPanel.inFreeCamMode && FreeCamPanel.freeCamScript.enabled;
        }

#if MONO
#pragma warning disable ULib004
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Input), nameof(Input.GetKey), [typeof(KeyCode)])]
        [HarmonyPatch(typeof(Input), nameof(Input.GetKeyUp), [typeof(KeyCode)])]
        [HarmonyPatch(typeof(Input), nameof(Input.GetKeyDown), [typeof(KeyCode)])]
        public static bool KeyCode_Eat(KeyCode __0, ref bool __result)
        {
            if (ShouldEatInput())
            {
                __result = false;
                return false;
            }

            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Input), nameof(Input.GetKey), [typeof(string)])]
        [HarmonyPatch(typeof(Input), nameof(Input.GetKeyUp), [typeof(string)])]
        [HarmonyPatch(typeof(Input), nameof(Input.GetKeyDown), [typeof(string)])]
        [HarmonyPatch(typeof(Input), nameof(Input.GetButton), [typeof(string)])]
        [HarmonyPatch(typeof(Input), nameof(Input.GetButtonUp), [typeof(string)])]
        [HarmonyPatch(typeof(Input), nameof(Input.GetButtonDown), [typeof(string)])]
        public static bool String_Eat(string __0, ref bool __result)
        {
            if (ShouldEatInput())
            {
                __result = false;
                return false;
            }

            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Input), nameof(Input.GetAxis), [typeof(string)])]
        [HarmonyPatch(typeof(Input), nameof(Input.GetAxisRaw), [typeof(string)])]
        public static bool Axis_Eat(string __0, ref float __result)
        {
            if (ShouldEatInput())
            {
                __result = 0.0f;
                return false;
            }

            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Input), nameof(Input.anyKey), MethodType.Getter)]
        [HarmonyPatch(typeof(Input), nameof(Input.anyKeyDown), MethodType.Getter)]
        public static bool Getter_bool_Eat(ref bool __result)
        {
            if (ShouldEatInput())
            {
                __result = false;
                return false;
            }

            return true;
        }
#pragma warning restore ULib004
#endif
    }
}
