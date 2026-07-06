namespace OriathHub.Plugins.HealthBars
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Numerics;
    using Coroutine;
    using ImGuiNET;
    using Newtonsoft.Json;
    using OriathHub.CoroutineEvents;
    using OriathHub.Plugin;
    using OriathHub.RemoteEnums;
    using OriathHub.RemoteEnums.Entity;
    using OriathHub.RemoteObjects.Components;
    using OriathHub.RemoteObjects.States.InGameStateObjects;
    using OriathHub.Utils;

    /// <summary>
    ///     HealthBars plugin.
    /// </summary>
    public sealed class HealthBars : PluginBase
    {
        private readonly List<string> textureToValidate = new()
        {
            "full_bar.png",
            "hollow_bar.png"
        };

        private HealthBarsSettings Settings = new();

        private int poiMonsterConfigToDelete = 0;
        private int poiMonsterConfigToAdd = 0;
        private float graduationsThickness = 0f;
        private Vector2 fontSize = Vector2.Zero;

        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");

        private string TexturesPath => Path.Join(this.DllDirectory, "Textures");

        private readonly TextureLoader textures = new();

        private readonly Dictionary<uint, Vector2> bPositions = new();

        // Per-monster DPS sampling state, keyed by entity id. Populated only for monsters and
        // cleared on area change (like bPositions); stale entries are pruned each sample tick.
        private readonly Dictionary<uint, DpsTracker> dpsTrackers = new();
        private readonly List<uint> staleDpsKeys = new();

        private ActiveCoroutine? onAreaChange = null;
        private ActiveCoroutine? onDpsSample = null;

        /// <inheritdoc />
        public override string Name => "Health Bars";

        /// <inheritdoc />
        public override string Description => "Draws health bars above entities in the game world.";

        /// <inheritdoc />
        public override string Author => "OriathHub";

        /// <inheritdoc />
        public override string Version => "1.0.0";

        /// <inheritdoc />
        public override void DrawSettings()
        {
            ImGui.Text("Turn off in game health bars for best result.");
            ImGui.Text("Enable/Disable plugin to reload textures.");
            ImGui.Text($"Total Textures loaded: {this.textures.TotalTexturesLoaded}");
            if (ImGui.CollapsingHeader("Common Configuration"))
            {
                if (ImGui.BeginTable("common_config_table", 2))
                {
                    ImGui.TableNextColumn();
                    ImGui.Checkbox("Draw healthbars in town", ref this.Settings.DrawInTown);
                    ImGui.TableNextColumn();
                    ImGui.Checkbox("Draw healthbars in hideout", ref this.Settings.DrawInHideout);
                    ImGui.TableNextColumn();
                    ImGui.Checkbox("Draw healthbars when game is in background", ref this.Settings.DrawWhenGameInBackground);
                    ImGui.TableNextColumn();
                    ImGui.Checkbox("Interpolate position", ref this.Settings.InterpolatePosition);
                    ImGuiHelper.ToolTip("Enable this if your healthbar is stuttering.");
                    if (this.Settings.InterpolatePosition)
                    {
                        if (ImGui.DragInt("Interpolation Rate", ref this.Settings.InterpolationRate, 1f, 1, 1000))
                        {
                            if (this.Settings.InterpolationRate <= 0)
                            {
                                this.Settings.InterpolationRate = 1;
                            }
                            else if (this.Settings.InterpolationRate >= 1000)
                            {
                                this.Settings.InterpolationRate = 1000;
                            }
                        }
                    }

                    ImGui.TableNextColumn();
                    ImGui.Text("white       magic      rare         unique");
                    ImGui.DragInt4("Cull Strike (%health)", ref this.Settings.CullingStrikeRangePerRarity[0], 1, 0, 100);
                    ImGui.TableNextColumn();
                    ImGui.Checkbox("Show mana rather than ES on self player", ref this.Settings.ShowManaRatherThanESOnSelf);
                    ImGui.TableNextColumn();
                    ImGui.DragFloat("Monster DPS window (s)", ref this.Settings.DpsWindowSeconds, 0.05f, 0.25f, 10f);
                    ImGuiHelper.ToolTip("Sliding window over which monster DPS is averaged. " +
                        "Shorter = more responsive, longer = smoother.");
                    ImGui.TableNextColumn();
                    ImGui.Checkbox("DPS text outline", ref this.Settings.DpsOutline);
                    if (this.Settings.DpsOutline)
                    {
                        ImGui.TableNextColumn();
                        ImGui.DragFloat("Outline thickness", ref this.Settings.DpsOutlineThickness, 0.1f, 1f, 3f);
                        ImGui.TableNextColumn();
                        ImGui.ColorEdit4("Outline color", ref this.Settings.DpsOutlineColor);
                    }

                    ImGui.EndTable();
                }
            }

            if (ImGui.CollapsingHeader("Monster Configuration"))
            {
                this.DrawMonsterConfig();
            }

            if (ImGui.CollapsingHeader("Player Configuration"))
            {
                this.DrawPlayerConfig();
            }
        }

        private void DrawMonsterConfig()
        {
            if (ImGui.BeginTabBar("monster_config"))
            {
                foreach (var item in this.Settings.Monster)
                {
                    if (ImGui.BeginTabItem(item.Key))
                    {
                        item.Value.Draw(true);
                        ImGui.EndTabItem();
                    }
                }

                ImGui.EndTabBar();
            }
        }

        private void DrawPlayerConfig()
        {
            if (ImGui.BeginTabBar("player_config"))
            {
                foreach (var item in this.Settings.Player)
                {
                    if (ImGui.BeginTabItem(item.Key))
                    {
                        item.Value.Draw();
                        ImGui.EndTabItem();
                    }
                }

                ImGui.EndTabBar();
            }
        }

        /// <inheritdoc />
        public override void DrawAdvancedSettings()
        {
            if (ImGui.CollapsingHeader("POI Configuration"))
            {
                this.DrawPOIConfig();
            }
        }

        private void DrawPOIConfig()
        {
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10);
                if (ImGui.InputInt("Group Number##poimonsterconfig", ref this.poiMonsterConfigToAdd) && this.poiMonsterConfigToAdd < 0)
                {
                    this.poiMonsterConfigToAdd = 0;
                }

                ImGui.SameLine();
                if (ImGui.Button("Add"))
                {
                    this.Settings.POIMonster.TryAdd(this.poiMonsterConfigToAdd, new());
                }

                if (ImGui.BeginTabBar("poimonster_config", ImGuiTabBarFlags.AutoSelectNewTabs))
                {
                    foreach (var conf in this.Settings.POIMonster)
                    {
                        var text = conf.Key < 0 ? "Default" : $"Group {conf.Key}";
                        var shouldNotDelete = true;
                        if (ImGui.BeginTabItem(text, ref shouldNotDelete, ImGuiTabItemFlags.NoAssumedClosure))
                        {
                            conf.Value.Draw(true);
                            ImGui.EndTabItem();
                        }

                        if (conf.Key >= 0 && !shouldNotDelete)
                        {
                            this.poiMonsterConfigToDelete = conf.Key;
                            ImGui.OpenPopup("POIConfigHealthbarDeleteConfirmation");
                        }
                    }

                    this.DrawConfirmationPopup();
                    ImGui.EndTabBar();
                }
        }

        /// <inheritdoc />
        public override IEnumerable<SettingSearchEntry> GetSearchableSettings() => new[]
        {
            new SettingSearchEntry("Common Configuration", "Draw healthbars in town",
                () => ImGui.Checkbox("Draw healthbars in town", ref this.Settings.DrawInTown)),
            new SettingSearchEntry("Common Configuration", "Draw healthbars in hideout",
                () => ImGui.Checkbox("Draw healthbars in hideout", ref this.Settings.DrawInHideout)),
            new SettingSearchEntry("Common Configuration", "Draw healthbars when game is in background",
                () => ImGui.Checkbox("Draw healthbars when game is in background", ref this.Settings.DrawWhenGameInBackground)),
            new SettingSearchEntry("Common Configuration", "Interpolate position", () =>
            {
                ImGui.Checkbox("Interpolate position", ref this.Settings.InterpolatePosition);
                ImGuiHelper.ToolTip("Enable this if your healthbar is stuttering.");
                if (this.Settings.InterpolatePosition)
                {
                    if (ImGui.DragInt("Interpolation Rate", ref this.Settings.InterpolationRate, 1f, 1, 1000))
                    {
                        if (this.Settings.InterpolationRate <= 0)
                            this.Settings.InterpolationRate = 1;
                        else if (this.Settings.InterpolationRate >= 1000)
                            this.Settings.InterpolationRate = 1000;
                    }
                }
            }, "interpolation rate stutter smooth"),
            new SettingSearchEntry("Common Configuration", "Cull Strike (%health)", () =>
            {
                ImGui.Text("white       magic      rare         unique");
                ImGui.DragInt4("Cull Strike (%health)", ref this.Settings.CullingStrikeRangePerRarity[0], 1, 0, 100);
            }, "culling strike rarity white magic rare unique"),
            new SettingSearchEntry("Common Configuration", "Show mana rather than ES on self player",
                () => ImGui.Checkbox("Show mana rather than ES on self player", ref this.Settings.ShowManaRatherThanESOnSelf), "mana es energy shield self"),
            new SettingSearchEntry("Common Configuration", "Monster DPS window (s)",
                () => ImGui.DragFloat("Monster DPS window (s)", ref this.Settings.DpsWindowSeconds, 0.05f, 0.25f, 10f),
                "dps damage per second window drained monster"),
            new SettingSearchEntry("Common Configuration", "DPS text outline", () =>
            {
                ImGui.Checkbox("DPS text outline", ref this.Settings.DpsOutline);
                if (this.Settings.DpsOutline)
                {
                    ImGui.DragFloat("Outline thickness", ref this.Settings.DpsOutlineThickness, 0.1f, 1f, 3f);
                    ImGui.ColorEdit4("Outline color", ref this.Settings.DpsOutlineColor);
                }
            }, "dps text outline border thickness color legibility"),

            new SettingSearchEntry("Monster Configuration", "Monster Configuration", this.DrawMonsterConfig,
                "monster rarity white magic rare unique bar color"),
            new SettingSearchEntry("Player Configuration", "Player Configuration", this.DrawPlayerConfig,
                "player self other bar color"),
            new SettingSearchEntry("POI Configuration (Advanced)", "POI Configuration", this.DrawPOIConfig,
                "poi monster group config point of interest"),
        };

        /// <inheritdoc />
        public override void DrawUI()
        {
            if (Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                return;
            }

            var cAreaInstance = Core.States.InGameStateObject.CurrentAreaInstance;
            var cWorldInstance = Core.States.InGameStateObject.CurrentWorldInstance;
            if ((!this.Settings.DrawInTown && cWorldInstance.AreaDetails.IsTown) ||
                (!this.Settings.DrawInHideout && cWorldInstance.AreaDetails.IsHideout))
            {
                return;
            }

            if (!this.Settings.DrawWhenGameInBackground && !FocusHelper.IsGameOrOverlayForeground())
            {
                return;
            }

            if (Core.States.InGameStateObject.GameUi.IsAnyLargePanelOpen)
            {
                return;
            }

            this.UpdateOncePerDraw();
            foreach (var entity in cAreaInstance.AwakeEntities)
            {
                if (!entity.Value.IsValid || entity.Value.EntityState == EntityStates.Useless ||
                    entity.Value.EntityType == EntityTypes.Renderable ||
                    entity.Value.EntityState == EntityStates.PinnacleBossHidden)
                {
                    continue;
                }

                switch (entity.Value.EntityType)
                {
                    case EntityTypes.Player:
                        if (entity.Value.EntitySubtype == EntitySubtypes.PlayerOther)
                        {
                            if (entity.Value.EntityState == EntityStates.PlayerLeader)
                            {
                                this.DrawHealthbar(entity.Value, this.Settings.Player["leader"], (int)Rarity.Rare);
                            }
                            else
                            {
                                this.DrawHealthbar(entity.Value, this.Settings.Player["member"], (int)Rarity.Rare);
                            }
                        }
                        else
                        {
                            this.DrawHealthbar(entity.Value, this.Settings.Player["self"], (int)Rarity.Rare, true);
                        }

                        break;
                    case EntityTypes.Monster:
                        // PoE2 tags decorative effigies and ground daemons with a Monster component
                        // but leaves them non-targetable; skip those (re-checked every frame, so a
                        // monster gets its bar the moment it becomes targetable). Friendly monsters
                        // (allies/minions) aren't player-targetable, so they stay exempt below.
                        var monsterTargetable = entity.Value.TryGetComponent<Targetable>(out var monTgt) && monTgt.IsTargetable;
                        // Defense-in-depth: ShouldForceMonsterRefresh can briefly revive a Useless
                        // dead monster (e.g. a death-animation spectre with active buffs) before
                        // CalculateEntityState re-sets it to Useless. Skip dead entities here too.
                        if (entity.Value.TryGetComponent<Life>(out var monLife) && !monLife.IsAlive)
                        {
                            break;
                        }

                        if (IsMonsterModHelper(entity.Value))
                        {
                            break;
                        }

                        if (entity.Value.EntitySubtype == EntitySubtypes.POIMonster)
                        {
                            if (!monsterTargetable)
                            {
                                break;
                            }

                            if (!this.Settings.POIMonster.TryGetValue(entity.Value.EntityCustomGroup, out var poiConfig))
                            {
                                poiConfig = this.Settings.POIMonster[-1];
                            }

                            this.DrawHealthbar(entity.Value, poiConfig,
                                entity.Value.TryGetComponent<ObjectMagicProperties>(out var oComp) ?
                                (int)oComp.Rarity :
                                (int)Rarity.Rare);
                        }
                        else if (entity.Value.EntityState == EntityStates.MonsterFriendly)
                        {
                            this.DrawHealthbar(entity.Value, this.Settings.Monster["friendly"], (int)Rarity.Rare);
                        }
                        else if (entity.Value.TryGetComponent<ObjectMagicProperties>(out var oComp))
                        {
                            if (!monsterTargetable)
                            {
                                break;
                            }

                            switch (oComp.Rarity)
                            {
                                case Rarity.Normal:
                                    this.DrawHealthbar(entity.Value, this.Settings.Monster["white"], (int)Rarity.Normal);
                                    break;
                                case Rarity.Magic:
                                    this.DrawHealthbar(entity.Value, this.Settings.Monster["magic"], (int)Rarity.Magic);
                                    break;
                                case Rarity.Rare:
                                    this.DrawHealthbar(entity.Value, this.Settings.Monster["rare"], (int)Rarity.Rare);
                                    break;
                                case Rarity.Unique:
                                    this.DrawHealthbar(entity.Value, this.Settings.Monster["unique"], (int)Rarity.Unique);
                                    break;
                            }
                        }

                        break;
                }
            }
        }

        private static bool IsMonsterModHelper(Entity entity)
        {
            // Entities that die on a fixed timer are daemon/controller helpers, never combat targets.
            if (entity.TryGetComponent<DiesAfterTime>(out _))
            {
                return true;
            }

            if (entity.Path.StartsWith("Metadata/Monsters/MonsterMods/", StringComparison.Ordinal))
            {
                return true;
            }

            // Use the cached overload (audit P-5): a monster's mod names are populated once when the
            // component address changes and never after, so re-reading is wasteful — and the
            // uncached path constructs a fresh wrapper whose ctor re-reads every mod vector + stats,
            // every frame, for every monster. DrawUI caches this same component for the rarity check
            // immediately below, so caching here is both correct and free.
            return entity.TryGetComponent<ObjectMagicProperties>(out var oComp) &&
                oComp.ModNames.Contains("RateLimitedDaemon") &&
                oComp.ModNames.Contains("MonsterNoDropsOrExperience");
        }

        /// <inheritdoc />
        public override void OnDisable()
        {
            this.textures.cleanup(this.TexturesPath);
            this.onAreaChange?.Cancel();
            this.onAreaChange = null;
            this.onDpsSample?.Cancel();
            this.onDpsSample = null;
            this.dpsTrackers.Clear();
        }

        /// <inheritdoc />
        public override void OnEnable(bool isGameOpened)
        {
            this.textures.Load(this.TexturesPath);
            if (File.Exists(this.SettingPathname))
            {
                var content = File.ReadAllText(this.SettingPathname);
                this.Settings = JsonConvert.DeserializeObject<HealthBarsSettings>(content) ?? new HealthBarsSettings();
            }

            for (var i = 0; i < this.textureToValidate.Count; i++)
            {
                if (!this.textures.TextureKeys.Contains(this.textureToValidate[i]))
                {
                    throw new Exception($"Missing texture file {this.textureToValidate[i]} in {this.TexturesPath} folder.");
                }
            }

            // StartCoroutine ties this to the plugin lifetime so the host force-cancels it on
            // disable/reload/unload even if OnDisable is skipped or throws.
            this.onAreaChange = this.StartCoroutine(this.OnAreaChange());
            this.onDpsSample = this.StartCoroutine(this.SampleDps());
        }

        /// <inheritdoc />
        public override void SaveSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.SettingPathname) ?? string.Empty);
            var settingsData = JsonConvert.SerializeObject(this.Settings, Formatting.Indented);
            File.WriteAllText(this.SettingPathname, settingsData);
        }

        private void DrawHealthbar(Entity entity, Config healthbarConfig, int rarity, bool isSelf = false)
        {
            if (!healthbarConfig.Enable)
            {
                return;
            }

            if (!entity.TryGetComponent<Render>(out var rComp))
            {
                return;
            }

            var curPos = rComp.WorldPosition;
            curPos.Z -= rComp.ModelBounds.Z + healthbarConfig.Shift.Y;
            var location = Core.States.InGameStateObject.CurrentWorldInstance.WorldToScreen(curPos, curPos.Z);
            location.X += healthbarConfig.Shift.X;
            if (!entity.TryGetComponent<Life>(out var hComp))
            {
                return;
            }

            if (this.Settings.InterpolatePosition)
            {
                if (this.bPositions.TryGetValue(entity.Id, out var prevLocation))
                {
                    location = MathHelper.Lerp(prevLocation, location, this.Settings.InterpolationRate / 1000f);
                }

                this.bPositions[entity.Id] = location;
            }

            var ptr = ImGui.GetBackgroundDrawList();
            var start = location - healthbarConfig.HalfOfScale;
            var end = location + healthbarConfig.HalfOfScale;

            ptr.AddRectFilled(start, end, ImGuiHelper.Color(healthbarConfig.BackgroundColor));

            // Ward is the last-resort pool "below" Life (damage order: ES > Life > Ward). In
            // BehindLife mode draw it first as the bottom layer - the Health/ES bars overlay it,
            // and it's revealed on the right edge as Life depletes. SeparateBar mode draws its
            // own bar below the main one (see after the ES block) so ward stays fully visible.
            if (hComp.Ward.Total > 0 && healthbarConfig.WardMode == WardDisplayMode.BehindLife)
            {
                var (ward_ptr, _, _) = this.textures.GetTexture(this.textureToValidate[0]);
                var wardPercent = hComp.Ward.CurrentInPercent();
                ptr.AddImage(ward_ptr, start, end - (Vector2.UnitX * healthbarConfig.Scale * (100 - wardPercent) / 100f),
                    Vector2.Zero, Vector2.One,
                    ImGuiHelper.Color(healthbarConfig.WardColor));
            }

            var (hb_ptr, _, _) = this.textures.GetTexture(this.textureToValidate[0]);
            var hPercent = hComp.Health.CurrentInPercent();
            ptr.AddImage(hb_ptr, start, end - (Vector2.UnitX * healthbarConfig.Scale * (100 - hPercent) / 100f), Vector2.Zero, Vector2.One,
                (hPercent > this.Settings.CullingStrikeRangePerRarity[rarity] || !healthbarConfig.ShowCullStrike) ?
                ImGuiHelper.Color(healthbarConfig.HealthbarColor) :
                0xFFFFFFFF);

            if (isSelf && this.Settings.ShowManaRatherThanESOnSelf)
            {
                var (es_ptr, _, _) = this.textures.GetTexture(this.textureToValidate[1]);
                ptr.AddImage(es_ptr, start, end - (Vector2.UnitX * healthbarConfig.Scale * (100 - hComp.Mana.CurrentInPercent()) / 100f),
                    Vector2.Zero, Vector2.One,
                    ImGuiHelper.Color(healthbarConfig.ESColor));
            }
            else
            {
                if (hComp.EnergyShield.Total > 0)
                {
                    var (es_ptr, _, _) = this.textures.GetTexture(this.textureToValidate[1]);
                    ptr.AddImage(es_ptr, start, end - (Vector2.UnitX * healthbarConfig.Scale * (100 - hComp.EnergyShield.CurrentInPercent()) / 100f),
                        Vector2.Zero, Vector2.One,
                        ImGuiHelper.Color(healthbarConfig.ESColor));
                }
            }

            // Resource view: a dedicated ward bar directly below the main bar, always showing the
            // true ward percentage independent of Life/ES (for skills that consume ward).
            if (hComp.Ward.Total > 0 && healthbarConfig.WardMode == WardDisplayMode.SeparateBar)
            {
                var (ward_ptr, _, _) = this.textures.GetTexture(this.textureToValidate[0]);
                var wardPercent = hComp.Ward.CurrentInPercent();
                var wardStart = new Vector2(start.X, end.Y + 1f);
                var wardEnd = new Vector2(end.X - (healthbarConfig.Scale.X * (100 - wardPercent) / 100f),
                    end.Y + 1f + healthbarConfig.WardBarHeight);
                ptr.AddImage(ward_ptr, wardStart, wardEnd, Vector2.Zero, Vector2.One,
                    ImGuiHelper.Color(healthbarConfig.WardColor));
            }

            var tmp = start - Vector2.UnitY;
            for (var i = 0; i < healthbarConfig.Graduations; i++)
            {
                tmp.X += healthbarConfig.GraduationsLocationStart;
                ptr.AddLine(tmp, tmp + healthbarConfig.GraduationsLocationEnd, 0xFF000000, this.graduationsThickness);
            }

            if (healthbarConfig.ShowText)
            {
                var textValue = hComp.Health.Current + hComp.EnergyShield.Current;
                if (healthbarConfig.ShowWardInText)
                {
                    textValue += hComp.Ward.Current;
                }

                ptr.AddText(start - this.fontSize, ImGuiHelper.Color(healthbarConfig.TextColor),
                    this.healthToHumanReadable(textValue));
            }

            // DPS readout below the bar. Only monsters get trackers (see SampleDps), so player
            // bars never have an entry here even though the flag lives on the shared Config.
            if (healthbarConfig.ShowDps && this.dpsTrackers.TryGetValue(entity.Id, out var dpsTracker))
            {
                var dps = dpsTracker.CurrentDps();
                if (dps > 0f)
                {
                    var dpsY = end.Y;
                    if (hComp.Ward.Total > 0 && healthbarConfig.WardMode == WardDisplayMode.SeparateBar)
                    {
                        // Clear the dedicated ward bar that sits directly under the main bar.
                        dpsY += 1f + healthbarConfig.WardBarHeight;
                    }

                    AddTextOutlined(ptr, new Vector2(start.X, dpsY + 1f),
                        ImGuiHelper.Color(healthbarConfig.DpsColor), DpsToHumanReadable(dps),
                        ImGuiHelper.Color(this.Settings.DpsOutlineColor),
                        this.Settings.DpsOutline ? this.Settings.DpsOutlineThickness : 0f);
                }
            }
        }

        // ImGui has no outlined text, so draw the string in the outline colour at the eight
        // offsets around the centre (4 cardinal + 4 diagonal), then the coloured text on top -
        // a cheap, legible border over any background. thickness <= 0 skips the outline.
        private static void AddTextOutlined(ImDrawListPtr ptr, Vector2 pos, uint color, string text,
            uint outlineColor, float thickness)
        {
            if (thickness > 0f)
            {
                ptr.AddText(pos + new Vector2(-thickness, 0f), outlineColor, text);
                ptr.AddText(pos + new Vector2(thickness, 0f), outlineColor, text);
                ptr.AddText(pos + new Vector2(0f, -thickness), outlineColor, text);
                ptr.AddText(pos + new Vector2(0f, thickness), outlineColor, text);
                ptr.AddText(pos + new Vector2(-thickness, -thickness), outlineColor, text);
                ptr.AddText(pos + new Vector2(thickness, -thickness), outlineColor, text);
                ptr.AddText(pos + new Vector2(-thickness, thickness), outlineColor, text);
                ptr.AddText(pos + new Vector2(thickness, thickness), outlineColor, text);
            }

            ptr.AddText(pos, color, text);
        }

        private static string DpsToHumanReadable(float value)
        {
            if (value >= 1_000_000f)
            {
                return $"{(value / 1_000_000f):0.00}M/s";
            }
            else if (value >= 1_000f)
            {
                return $"{(value / 1_000f):0.00}K/s";
            }
            else
            {
                return $"{value:0}/s";
            }
        }

        private void UpdateOncePerDraw()
        {
            this.graduationsThickness = ImGui.GetFontSize() / 9f;
            this.fontSize = new(0f, ImGui.GetFontSize());
        }

        private string healthToHumanReadable(int value)
        {
            if (value >= 1_000_000)
            {
                return $"{(value / 1_000_000f):0.00}M";
            }
            else if (value >= 1_000)
            {
                return $"{(value / 1_000f):0.00}K";
            }
            else
            {
                return $"{value}";
            }
        }

        private void DrawConfirmationPopup()
        {
            ImGui.SetNextWindowPos(new Vector2(Core.Overlay.Size.Width / 3f, Core.Overlay.Size.Height / 3f));
            if (ImGui.BeginPopup("POIConfigHealthbarDeleteConfirmation"))
            {
                ImGui.Text($"Do you want to delete group {this.poiMonsterConfigToDelete} POI Monster healthbar config?");
                ImGui.Separator();
                if (ImGui.Button("Yes",
                    new Vector2(ImGui.GetContentRegionAvail().X / 2f, ImGui.GetTextLineHeight() * 2)))
                {
                    _ = this.Settings.POIMonster.Remove(poiMonsterConfigToDelete);
                    ImGui.CloseCurrentPopup();
                }

                ImGui.SameLine();
                if (ImGui.Button("No", new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetTextLineHeight() * 2)))
                {
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndPopup();
            }
        }

        private IEnumerator<Wait> OnAreaChange()
        {
            while (true)
            {
                yield return new Wait(RemoteEvents.AreaChanged);
                this.bPositions.Clear();
                this.dpsTrackers.Clear();
            }
        }

        /// <summary>
        ///     Samples every monster's effective HP pool (Life + ES + Ward) once per data phase and
        ///     feeds it to a per-entity <see cref="DpsTracker"/>. Runs on PerFrameDataUpdate (not the
        ///     render loop) so measurement is decoupled from whether bars are actually drawn.
        /// </summary>
        private IEnumerator<Wait> SampleDps()
        {
            while (true)
            {
                yield return new Wait(OriathEvents.PerFrameDataUpdate);
                if (Core.States.GameCurrentState != GameStateTypes.InGameState)
                {
                    continue;
                }

                var now = Stopwatch.GetTimestamp();
                var windowTicks = (long)(Math.Max(0.25f, this.Settings.DpsWindowSeconds) * Stopwatch.Frequency);
                // Drop trackers whose entity hasn't been seen for two windows (despawned / out of
                // range) so the dictionary stays bounded within a long-lived area.
                var staleTicks = windowTicks * 2;

                var cAreaInstance = Core.States.InGameStateObject.CurrentAreaInstance;
                foreach (var kv in cAreaInstance.AwakeEntities)
                {
                    var entity = kv.Value;
                    if (entity.EntityType != EntityTypes.Monster || !entity.IsValid)
                    {
                        continue;
                    }

                    if (!entity.TryGetComponent<Life>(out var life) || !life.IsAlive)
                    {
                        continue;
                    }

                    var pool = life.Health.Current + life.EnergyShield.Current + life.Ward.Current;
                    if (!this.dpsTrackers.TryGetValue(entity.Id, out var tracker))
                    {
                        tracker = new DpsTracker();
                        this.dpsTrackers[entity.Id] = tracker;
                    }

                    tracker.Sample(pool, now, windowTicks);
                }

                this.staleDpsKeys.Clear();
                foreach (var kv in this.dpsTrackers)
                {
                    if (now - kv.Value.LastTouchedTicks > staleTicks)
                    {
                        this.staleDpsKeys.Add(kv.Key);
                    }
                }

                for (var i = 0; i < this.staleDpsKeys.Count; i++)
                {
                    this.dpsTrackers.Remove(this.staleDpsKeys[i]);
                }
            }
        }

        /// <summary>
        ///     Tracks the effective-HP samples of a single monster over a sliding time window and
        ///     derives a gross "drained per second" rate. Only decreases (damage) are counted; pool
        ///     increases from regen, ES recharge or ward refresh are ignored, so the reading reflects
        ///     damage dealt rather than net change.
        /// </summary>
        private sealed class DpsTracker
        {
            // One sample per data tick, trimmed to the window. A 1 s window at 60-144 fps holds
            // ~60-150 entries - cheap - and the Queue trims in O(1), so no throttling is needed
            // (an earlier interval throttle silently stalled the buffer at a single sample).
            private readonly Queue<(long Ticks, int Pool)> samples = new();

            /// <summary>Timestamp of the most recent <see cref="Sample"/> call (for staleness pruning).</summary>
            public long LastTouchedTicks { get; private set; }

            /// <summary>Records a new pool reading and trims samples outside the window.</summary>
            public void Sample(int pool, long now, long windowTicks)
            {
                // A gap longer than the window means we lost continuity (plugin idle, entity
                // re-appeared under a reused id) - restart rather than charge one huge delta.
                if (this.samples.Count > 0 && now - this.LastTouchedTicks > windowTicks)
                {
                    this.samples.Clear();
                }

                this.LastTouchedTicks = now;
                this.samples.Enqueue((now, pool));

                var cutoff = now - windowTicks;
                while (this.samples.Count > 1 && this.samples.Peek().Ticks < cutoff)
                {
                    this.samples.Dequeue();
                }
            }

            /// <summary>Gross effective-HP drained per second across the retained window.</summary>
            public float CurrentDps()
            {
                if (this.samples.Count < 2)
                {
                    return 0f;
                }

                var totalDrop = 0L;
                var hasPrev = false;
                var prevPool = 0;
                long firstTicks = 0;
                long lastTicks = 0;
                foreach (var (ticks, sPool) in this.samples)
                {
                    if (!hasPrev)
                    {
                        firstTicks = ticks;
                    }
                    else
                    {
                        var delta = prevPool - sPool;
                        if (delta > 0)
                        {
                            totalDrop += delta;
                        }
                    }

                    lastTicks = ticks;
                    prevPool = sPool;
                    hasPrev = true;
                }

                if (totalDrop <= 0)
                {
                    return 0f;
                }

                var spanTicks = lastTicks - firstTicks;
                if (spanTicks <= 0)
                {
                    return 0f;
                }

                return totalDrop / (spanTicks / (float)Stopwatch.Frequency);
            }
        }
    }
}
