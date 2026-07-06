namespace OriathHub.Plugins.HealthBars
{
    using System.Numerics;
    using OriathHub.Utils;
    using ImGuiNET;
    using Newtonsoft.Json;

    /// <summary>
    ///     How the Ward pool is rendered on a healthbar.
    /// </summary>
    public enum WardDisplayMode
    {
        /// <summary>
        ///     Ward is the bottom layer behind Life; revealed on the right edge as Life depletes.
        ///     Last-resort defence view (original behaviour).
        /// </summary>
        BehindLife = 0,

        /// <summary>
        ///     Ward is drawn as its own thin bar below the main bar, always at its true percentage.
        ///     Resource view for skills that consume ward.
        /// </summary>
        SeparateBar = 1,

        /// <summary>
        ///     Ward is not drawn.
        /// </summary>
        Off = 2
    }

    /// <summary>
    ///     Saves config of each type of healthbar.
    /// </summary>
    public class Config
    {
        /// <summary>
        ///     Enables the healthbar
        /// </summary>
        public bool Enable;

        /// <summary>
        ///     Change texture if player can cull strike this healthbar.
        /// </summary>
        public bool ShowCullStrike;

        /// <summary>
        ///     Show the absolute Health + Es as text aboved the healthbar
        /// </summary>
        public bool ShowText;

        /// <summary>
        ///     Gets the color to apply on healthbar background.
        /// </summary>
        public Vector4 BackgroundColor;

        /// <summary>
        ///     Gets the color to apply on healthbar.
        /// </summary>
        public Vector4 HealthbarColor;

        /// <summary>
        ///     Gets the color to apply on ES bar.
        /// </summary>
        public Vector4 ESColor;

        /// <summary>
        ///     Gets the color to apply on the Ward bar.
        /// </summary>
        public Vector4 WardColor;

        /// <summary>
        ///     How the Ward pool is rendered. Defaults to <see cref="WardDisplayMode.BehindLife" />
        ///     so existing configs keep their look.
        /// </summary>
        public WardDisplayMode WardMode;

        /// <summary>
        ///     Height (px) of the dedicated ward bar used by <see cref="WardDisplayMode.SeparateBar" />.
        /// </summary>
        public float WardBarHeight;

        /// <summary>
        ///     Include the ward value in the absolute health text above the bar.
        /// </summary>
        public bool ShowWardInText;

        /// <summary>
        ///     Show a live DPS (effective HP drained per second) readout under the bar.
        ///     Monster bars only; the plugin never tracks DPS for player bars.
        /// </summary>
        public bool ShowDps;

        /// <summary>
        ///     Colour of the DPS readout text.
        /// </summary>
        public Vector4 DpsColor;

        /// <summary>
        ///     Gets the color of the next.
        /// </summary>
        public Vector4 TextColor;

        /// <summary>
        ///     Healthbar size multiplier
        /// </summary>
        public Vector2 Scale;

        /// <summary>
        ///     Healthbar position shift.
        /// </summary>
        public Vector2 Shift;

        /// <summary>
        ///     Gets the half of the scale value.
        /// </summary>
        public Vector2 HalfOfScale;

        /// <summary>
        ///     Total number of Graduations on this healthbar
        /// </summary>
        public int Graduations;

        /// <summary>
        ///     Stores the start location of any given Graduation.
        /// </summary>
        public float GraduationsLocationStart;

        /// <summary>
        ///     Stores the end location of any given Graduation.
        /// </summary>
        public Vector2 GraduationsLocationEnd;

        /// <summary>
        ///     Initializes a new instance of the <see cref="Config" /> class.
        /// </summary>
        /// <param name="healthbarcolor">color of the healthbar</param>
        /// <param name="graduations">number of graduations on the healthbar</param>
        /// <param name="showText">show the absolute Health + Es as text aboved the healthbar</param>
        /// <param name="sizeY">healthbar default size Y axis.</param>
        public Config(Vector4 healthbarcolor, int graduations, bool showText, float sizeY)
        {
            this.Enable = true;
            this.ShowCullStrike = true;
            this.ShowText = showText;
            this.BackgroundColor = new(Vector3.Zero, 1f);
            this.HealthbarColor = healthbarcolor;
            this.ESColor = new(0f, 1f, 1f, 1f);
            this.WardColor = new(1f, 1f, 1f, 1f);
            this.WardMode = WardDisplayMode.BehindLife;
            this.WardBarHeight = 8f;
            this.ShowWardInText = true;
            this.ShowDps = false;
            this.DpsColor = new(1f, 0.4f, 0.4f, 1f);
            this.TextColor = new(0f, 1f, 1f, 1f);
            this.Scale = new(128f, sizeY);
            this.HalfOfScale = this.Scale / 2;
            this.Shift = new(0f, 11f);
            this.Graduations = graduations;
            this.GraduationsLocationStart = 0f;
            this.GraduationsLocationEnd = Vector2.Zero;
            this.UpdateGrauationsLocationData();
        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="Config" /> class.
        /// </summary>
        /// <param name="healthbarcolor">color of the healthbar</param>
        /// <param name="sizeY">healthbar default size Y axis.</param>
        public Config(Vector4 healthbarcolor, float sizeY) :
            this(healthbarcolor, 0, false, sizeY) { }

        /// <summary>
        ///     Initializes a new instance of the <see cref="Config" /> class.
        /// </summary>
        /// <param name="healthbarcolor">color of the healthbar</param>
        /// <param name="graduations">number of graduations on the healthbar</param>
        public Config(Vector4 healthbarcolor, int graduations) :
            this(healthbarcolor, graduations, false, 8f) { }

        /// <summary>
        ///     Initializes a new instance of the <see cref="Config" /> class.
        /// </summary>
        /// <param name="healthbarcolor">color of the healthbar</param>
        public Config(Vector4 healthbarcolor) :
            this(healthbarcolor, 0) { }

        /// <summary>
        ///     Initializes a new instance of the <see cref="Config" /> class.
        /// </summary>
        [JsonConstructor]
        public Config() :
            this(Vector4.One) { }

        /// <summary>
        ///     Display the Config on imgui.
        /// </summary>
        /// <param name="supportsDps">
        ///     When true, exposes the DPS readout options. Only monster bars support DPS; player
        ///     bars pass false so the inert options are hidden.
        /// </param>
        public void Draw(bool supportsDps = false)
        {
            ImGui.Text("NOTE: For going above/below the limit, or for manual editing, press CTRL + Left Mouse Button click.");
            if (ImGui.BeginTable("config_table", 2))
            {
                ImGui.TableNextColumn();
                ImGui.Checkbox("Enable Healthbar", ref this.Enable);
                ImGui.TableNextColumn();
                ImGui.Checkbox("Visualize Culling Strike Range", ref this.ShowCullStrike);
                ImGui.TableNextColumn();
                ImGui.Checkbox("Show health+ES (absolute) as text", ref this.ShowText);
                ImGui.TableNextColumn();
                ImGui.ColorEdit4("Text Color", ref this.TextColor);
                ImGui.TableNextColumn();
                if (Vector2SliderInt("Scale (x, y)", ImGui.GetColumnWidth(), ref this.Scale, 32, 500, 4, 128, ImGuiSliderFlags.Logarithmic))
                {
                    this.UpdateGrauationsLocationData();
                }

                ImGuiHelper.ToolTip("By default texture is of height 16, " +
                    "If increasing the Y axis ruins the texture, " +
                    "feel free to modify the texture height via your fav texture editor. " +
                    "This doesn't apply to x axis.");
                ImGui.TableNextColumn();
                Vector2SliderInt("Shift (x, y)", ImGui.GetColumnWidth(), ref this.Shift, -4000, 4000, -2500, 2500, ImGuiSliderFlags.Logarithmic);
                ImGui.TableNextColumn();
                ImGui.ColorEdit4("Healthbar", ref this.HealthbarColor);
                ImGui.TableNextColumn();
                ImGui.ColorEdit4("Background", ref this.BackgroundColor);
                ImGui.TableNextColumn();
                if (ImGui.DragInt("Gradation Marks", ref this.Graduations, 0.05f, 0, 9))
                {
                    this.UpdateGrauationsLocationData();
                }

                ImGuiHelper.ToolTip("Graduation thickness depends on Font size. Also, " +
                    "Gradation marks are expensive to draw, on non rare/unique monsters keep it to 0.");
                ImGui.TableNextColumn();
                ImGui.ColorEdit4("ES Bar", ref this.ESColor);
                ImGui.TableNextColumn();
                ImGui.ColorEdit4("Ward Bar", ref this.WardColor);
                ImGui.TableNextColumn();
                ImGuiHelper.EnumComboBox("Ward Display", ref this.WardMode);
                ImGuiHelper.ToolTip("Behind Life: ward hides under Life (last-resort defence). " +
                    "Separate Bar: a dedicated ward bar under the main bar, always visible (resource view). " +
                    "Off: don't draw ward.");
                if (this.WardMode == WardDisplayMode.SeparateBar)
                {
                    ImGui.TableNextColumn();
                    ImGui.DragFloat("Ward Bar Height", ref this.WardBarHeight, 0.1f, 2f, 64f);
                }

                ImGui.TableNextColumn();
                ImGui.Checkbox("Include ward in text", ref this.ShowWardInText);
                if (supportsDps)
                {
                    ImGui.TableNextColumn();
                    ImGui.Checkbox("Show DPS (drained/s)", ref this.ShowDps);
                    ImGuiHelper.ToolTip("Effective HP (Life + ES + Ward) drained per second, " +
                        "measured over the window set in Common Configuration.");
                    if (this.ShowDps)
                    {
                        ImGui.TableNextColumn();
                        ImGui.ColorEdit4("DPS Text Color", ref this.DpsColor);
                    }
                }

                ImGui.EndTable();
            }
        }

        private void UpdateGrauationsLocationData()
        {
            this.GraduationsLocationStart = this.Scale.X / (this.Graduations + 1);
            this.GraduationsLocationEnd = Vector2.UnitY * this.Scale.Y;
            this.HalfOfScale = this.Scale / 2;
        }

        /// <summary>
        ///     Draws two compact int sliders backed by a <see cref="Vector2" /> (X then Y),
        ///     with the label after the second slider. OriathHub's ImGuiHelper has no such
        ///     helper, so the plugin carries its own to match the upstream layout.
        /// </summary>
        private static bool Vector2SliderInt(string text, float itemWidth, ref Vector2 data,
            int min0, int max0, int min1, int max1, ImGuiSliderFlags flags)
        {
            var dataChanged = false;
            var dataX = (int)data.X;
            var dataY = (int)data.Y;
            ImGui.PushItemWidth(itemWidth / 3.1f);
            if (ImGui.SliderInt($"##{text}111", ref dataX, min0, max0, "%d", flags))
            {
                dataChanged = true;
                data.X = dataX;
            }

            ImGui.SameLine(0f, 5f);
            if (ImGui.SliderInt($"{text}##{text}222", ref dataY, min1, max1, "%d", flags))
            {
                dataChanged = true;
                data.Y = dataY;
            }

            ImGui.PopItemWidth();
            return dataChanged;
        }
    }
}
