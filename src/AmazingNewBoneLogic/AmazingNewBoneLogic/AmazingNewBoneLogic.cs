using System;
using UniRx;
using KKAPI;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using KKAPI.Maker;
using KKAPI.Chara;
using KKAPI.Studio;
using IllusionFixes;
using KKAPI.Maker.UI;
using BepInEx.Logging;
using KKAPI.Studio.UI;
using BepInEx.Configuration;
using KKAPI.Maker.UI.Sidebar;
using System.Collections.Generic;
using System.Linq;
using Studio;
using static HandCtrl;

namespace AmazingNewBoneLogic
{
    [BepInPlugin(GUID, PluginName, Version)]
    [BepInDependency(KoikatuAPI.GUID, "1.30")]
    [BepInDependency("KKABMX.Core", "3.2")]
    [BepInDependency("SliderHighlight", BepInDependency.DependencyFlags.SoftDependency)]
    class AmazingNewBoneLogic : BaseUnityPlugin
    {
        public const string PluginName = "AmazingNewBoneLogic";
        public const string GUID = "com.dreamhana.anbl";
        public const string Version = "1.0.0";

        internal new static ManualLogSource Logger;

        public static SidebarToggle SidebarToggle;

        public static AmazingNewBoneLogic Instance;

        public static ConfigEntry<bool> Debug { get; private set; }
        public static ConfigEntry<float> UIScaleModifier { get; private set; }
        public static ConfigEntry<float> UIMainScaleFactor { get; private set; }
        public static ConfigEntry<KeyboardShortcut> ShortcutOpen { get; private set; }

        public static ConfigEntry<KeyCode> UIDeleteNodeKey;
        public static ConfigEntry<KeyCode> UIDisableNodeKey;
        public static ConfigEntry<KeyCode> UISelectedTreeKey;
        public static ConfigEntry<KeyCode> UISelectNetworkKey;


        void Awake()
        {
            MakerAPI.MakerBaseLoaded += createMakerInteractables;
            Logger = base.Logger;


            CharacterApi.RegisterExtraBehaviour<AnblCharaController>(GUID);
            Instance = this;

            Debug = Config.Bind("Advanced", "Debug", false,
                new ConfigDescription("Whether to log detailed debug messages", null,
                    new KKAPI.Utilities.ConfigurationManagerAttributes { IsAdvanced = true }));
            UIScaleModifier = Config.Bind("UI", "UI Scale Factor", Screen.height <= 1080 ? 1.3f : 1f,
                new ConfigDescription("Additional Scale to apply to the UI",
                    new AcceptableValueRange<float>(0.5f, 2f)));
            UIMainScaleFactor = Config.Bind("UI", "Main UI Scale Factor", Screen.height <= 1080 ? 1.3f : 1f,
                new ConfigDescription("Scale factor applied to all standard IMGUI windows (Simple Mode, Bone Editor, etc.)",
                    new AcceptableValueRange<float>(0.5f, 2f)));
            UIDeleteNodeKey = Config.Bind("Keybinds", "Delete Node", KeyCode.Delete,
                "Key press to delete the selected node(s)");
            UIDeleteNodeKey.SettingChanged += KeyCodeSettingChanged;
            UIDisableNodeKey = Config.Bind("Keybinds", "Disable Node", KeyCode.D,
                "Key press to disable the selected node(s)");
            UIDisableNodeKey.SettingChanged += KeyCodeSettingChanged;
            UISelectedTreeKey = Config.Bind("Keybinds", "Select Tree", KeyCode.T,
                "Key press to expand the selection to all downstream nodes");
            UISelectedTreeKey.SettingChanged += KeyCodeSettingChanged;
            UISelectNetworkKey = Config.Bind("Keybinds", "Select Network", KeyCode.N,
                "Key press to expand the selection to all down and upstream nodes");
            UISelectNetworkKey.SettingChanged += KeyCodeSettingChanged;
            ShortcutOpen = Config.Bind("UI", "Open UI", new KeyboardShortcut(),
                new ConfigDescription("Keyboard shortcut to open / close the ANBL UI"));

            Hooks.SetupHooks();
            
        }

        private void KeyCodeSettingChanged(object sender, EventArgs e)
        {
            CharacterApi.GetRegisteredBehaviour(GUID).Instances
                .Do(ctrl => ((AnblCharaController)ctrl).UpdateGraphKeybinds());
        }

        void Start()
        {
            if (StudioAPI.InsideStudio)
            {
                StudioLoaded();
            }
        }

        void Update()
        {
            if (ShortcutOpen.Value.IsDown())
            {
                if (MakerAPI.InsideMaker)
                {
                    AnblCharaController ctrl = MakerAPI.GetCharacterControl().GetComponent<AnblCharaController>();
                    SidebarToggle.SetValue(!ctrl?.displayGraph ?? false);
                }
                else if (StudioAPI.InsideStudio)
                {
                    List<OCIChar> chars = StudioAPI.GetSelectedCharacters().ToList();
                    if (chars.Count == 0)
                    {
                        Logger.LogMessage("Please select a character!");
                    }
                    else
                    {
                        AnblCharaController ctrl = chars[0].charInfo.GetComponent<AnblCharaController>();
                        if (ctrl?.displayGraph ?? false)
                        {
                            ctrl?.Hide();
                        }
                        else
                        {
                            ctrl?.Show(false);
                        }
                    }
                }
            }
        }

        private void StudioLoaded()
        {
            CurrentStateCategory currentStateCategory = StudioAPI.GetOrCreateCurrentStateCategory(null);
            currentStateCategory.AddControl(
                new CurrentStateCategorySwitch("Show ANBL",
                    c => c.GetChaControl().GetComponent<AnblCharaController>().displayGraph)).Value.Subscribe(
                display =>
                {
                    if (!display)
                        StudioAPI.GetSelectedControllers<AnblCharaController>().Do(controller => controller.Hide());
                    else
                        StudioAPI.GetSelectedControllers<AnblCharaController>().Do(controller =>
                            controller.Show(Input.GetKey(KeyCode.LeftShift)));
                });
            TimelineHelper.PopulateTimeline();
        }

        private void showGraphInMaker(bool b)
        {
            if (b)
            {
                MakerAPI.GetCharacterControl()?.GetComponent<AnblCharaController>()
                    ?.Show(Input.GetKey(KeyCode.LeftShift));
            }
            else MakerAPI.GetCharacterControl()?.GetComponent<AnblCharaController>()?.Hide();
        }
        
        internal static void UpdateMakerButtonVisibility()
        {
            // Do nothing
        }

        private void createMakerInteractables(object sender, RegisterCustomControlsEvent e)
        {
            SidebarToggle = e.AddSidebarControl(new SidebarToggle("Show ANBL", false, this));
            SidebarToggle.ValueChanged.Subscribe(delegate(bool b) { showGraphInMaker(b); });
        }

        internal CursorManager getMakerCursorMangaer()
        {
            return base.gameObject.GetComponent<CursorManager>();
        }
    }
}