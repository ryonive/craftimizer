using Craftimizer.Utils;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Craftimizer.Plugin;

internal static class ImGuiUtils
{
    private static readonly Stack<(Vector2 Min, Vector2 Max, float TopPadding)> GroupPanelLabelStack = new();

    // Adapted from https://github.com/ocornut/imgui/issues/1496#issuecomment-655048353
    // width = -1 -> size to parent
    // width = 0 -> size to content
    // returns available width (better since it accounts for the right side padding)
    // ^ only useful if width = -1
    public static float BeginGroupPanel(string name, float width)
    {
        // container group
        ImGui.BeginGroup();

        var itemSpacing = ImGui.GetStyle().ItemSpacing;
        var frameHeight = ImGui.GetFrameHeight();
        width = width < 0 ? ImGui.GetContentRegionAvail().X - (2 * itemSpacing.X) : width;
        var fullWidth = width > 0 ? width + (2 * itemSpacing.X) : 0;
        {
            using var noPadding = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, Vector2.Zero);
            using var noSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero);

            // inner group
            ImGui.BeginGroup();
            ImGui.Dummy(new Vector2(fullWidth, 0));
            ImGui.Dummy(new Vector2(itemSpacing.X, 0)); // shifts next group by is.x
            ImGui.SameLine(0, 0);

            // label group
            ImGui.BeginGroup();
            ImGui.Dummy(new Vector2(frameHeight / 2, 0)); // shifts text by fh/2
            ImGui.SameLine(0, 0);
            var textFrameHeight = ImGui.GetFrameHeight();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(name);
            GroupPanelLabelStack.Push((ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), textFrameHeight / 2f)); // push rect to stack
            ImGui.SameLine(0, 0);
            ImGui.Dummy(new Vector2(0f, textFrameHeight + itemSpacing.Y)); // shifts content by fh + is.y

            // content group
            ImGui.BeginGroup();
        }

        return width;
    }

    public static void EndGroupPanel()
    {
        var itemSpacing = ImGui.GetStyle().ItemSpacing;
        {
            using var noPadding = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, Vector2.Zero);
            using var noSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero);

            // content group
            ImGui.EndGroup();

            // label group
            ImGui.EndGroup();

            ImGui.SameLine(0, 0);
            // shifts full size by is (for rect placement)
            ImGui.Dummy(new(itemSpacing.X, 0));
            ImGui.Dummy(new(0, itemSpacing.Y * 2)); // * 2 for some reason (otherwise the bottom is too skinny)

            // inner group
            ImGui.EndGroup();

            var labelRect = GroupPanelLabelStack.Pop();
            var innerMin = ImGui.GetItemRectMin() + new Vector2(0, labelRect.TopPadding);
            var innerMax = ImGui.GetItemRectMax();

            (Vector2 Min, Vector2 Max) frameRect = (innerMin, innerMax);
            // add itemspacing padding on the label's sides
            labelRect.Min.X -= itemSpacing.X / 2;
            labelRect.Max.X += itemSpacing.X / 2;
            for (var i = 0; i < 4; ++i)
            {
                var (minClip, maxClip) = i switch
                {
                    0 => (new Vector2(float.NegativeInfinity), new Vector2(labelRect.Min.X, float.PositiveInfinity)),
                    1 => (new Vector2(labelRect.Max.X, float.NegativeInfinity), new Vector2(float.PositiveInfinity)),
                    2 => (new Vector2(labelRect.Min.X, float.NegativeInfinity), new Vector2(labelRect.Max.X, labelRect.Min.Y)),
                    3 => (new Vector2(labelRect.Min.X, labelRect.Max.Y), new Vector2(labelRect.Max.X, float.PositiveInfinity)),
                    _ => (Vector2.Zero, Vector2.Zero)
                };

                ImGui.PushClipRect(minClip, maxClip, true);
                ImGui.GetWindowDrawList().AddRect(
                    frameRect.Min, frameRect.Max,
                    ImGui.GetColorU32(ImGuiCol.Border),
                    itemSpacing.X);
                ImGui.PopClipRect();
            }

            ImGui.Dummy(Vector2.Zero);
        }

        ImGui.EndGroup();
    }

    private struct EndUnconditionally : ImRaii.IEndObject, IDisposable
    {
        private Action EndAction { get; }

        public bool Success { get; }

        public bool Disposed { get; private set; }

        public EndUnconditionally(Action endAction, bool success)
        {
            EndAction = endAction;
            Success = success;
            Disposed = false;
        }

        public void Dispose()
        {
            if (!Disposed)
            {
                EndAction();
                Disposed = true;
            }
        }
    }

    public static ImRaii.IEndObject GroupPanel(string name, float width, out float internalWidth)
    {
        internalWidth = BeginGroupPanel(name, width);
        return new EndUnconditionally(EndGroupPanel, true);
    }

    private static Vector2 UnitCircle(float theta)
    {
        var (s, c) = MathF.SinCos(theta);
        // SinCos positive y is downwards, but we want it upwards for ImGui
        return new Vector2(c, -s);
    }

    private static float Lerp(float a, float b, float t) =>
        MathF.FusedMultiplyAdd(b - a, t, a);

    private static void ArcSegment(Vector2 o, Vector2 prev, Vector2 cur, Vector2? next, float radius, float ratio, uint color)
    {
        var d = ImGui.GetWindowDrawList();

        d.PathLineTo(o + cur * radius);
        d.PathLineTo(o + prev * radius);
        d.PathLineTo(o + prev * radius * ratio);
        d.PathLineTo(o + cur * radius * ratio);
        if (next is { } nextValue)
            d.PathLineTo(o + nextValue * radius);
        d.PathFillConvex(color);
    }

    public static void Arc(float startAngle, float endAngle, float radius, float ratio, uint backgroundColor, uint filledColor, bool addDummy = true)
    {
        // Fix normals when drawing (for antialiasing)
        if (startAngle > endAngle)
            (startAngle, endAngle) = (endAngle, startAngle);

        var offset = ImGui.GetCursorScreenPos() + new Vector2(radius);

        var segments = ImGui.GetWindowDrawList()._CalcCircleAutoSegmentCount(radius);
        var incrementAngle = MathF.Tau / segments;
        var isFullCircle = (endAngle - startAngle) % MathF.Tau == 0;

        var prevA = startAngle;
        var prev = UnitCircle(prevA);
        for (var i = 1; i <= segments; ++i)
        {
            var a = startAngle + incrementAngle * i;
            var cur = UnitCircle(a);

            var nextA = a + incrementAngle;
            var next = UnitCircle(nextA);

            // full segment is background
            if (prevA >= endAngle)
            {
                // don't overlap with the first segment
                if (i == segments && !isFullCircle)
                    ArcSegment(offset, prev, cur, null, radius, ratio, backgroundColor);
                else
                    ArcSegment(offset, prev, cur, next, radius, ratio, backgroundColor);
            }
            // segment is partially filled
            else if (a > endAngle && !isFullCircle)
            {
                // we split the drawing in two
                var end = UnitCircle(endAngle);
                ArcSegment(offset, prev, end, null, radius, ratio, filledColor);
                ArcSegment(offset, end, cur, next, radius, ratio, backgroundColor);
                // set the previous segment to endAngle
                a = endAngle;
                cur = end;
            }
            // full segment is filled
            else
            {
                // if the next segment will be partially filled, the next segment will be the endAngle
                if (nextA > endAngle && !isFullCircle)
                {
                    var end = UnitCircle(endAngle);
                    ArcSegment(offset, prev, cur, end, radius, ratio, filledColor);
                }
                else
                    ArcSegment(offset, prev, cur, next, radius, ratio, filledColor);
            }
            prevA = a;
            prev = cur;
        }

        if (addDummy)
            ImGui.Dummy(new Vector2(radius * 2));
    }

    public static void ArcProgress(float value, float radiusInner, float radiusOuter, uint backgroundColor, uint filledColor)
    {
        Arc(MathF.PI / 2, MathF.PI / 2 - MathF.Tau * Math.Clamp(value, 0, 1), radiusInner, radiusOuter, backgroundColor, filledColor);
    }

    private sealed class SearchableComboData<T> where T : class
    {
        public readonly ImmutableArray<T> items;
        public List<T> filteredItems;
        public T selectedItem;
        public string input;
        public bool wasTextActive;
        public bool wasPopupActive;
        public CancellationTokenSource? cts;
        public Task? task;

        private readonly Func<T, string> getString;

        public SearchableComboData(IEnumerable<T> items, T selectedItem, Func<T, string> getString)
        {
            this.items = items.ToImmutableArray();
            filteredItems = new() { selectedItem };
            this.selectedItem = selectedItem;
            this.getString = getString;
            input = GetString(selectedItem);
        }

        public void SetItem(T selectedItem)
        {
            if (this.selectedItem != selectedItem)
            {
                input = GetString(selectedItem);
                this.selectedItem = selectedItem;
            }
        }

        public string GetString(T item) => getString(item);

        public void Filter()
        {
            cts?.Cancel();
            var inp = input;
            cts = new();
            var token = cts.Token;
            task = Task.Run(() => FilterTask(inp, token), token)
                .ContinueWith(t =>
            {
                if (cts.IsCancellationRequested)
                    return;

                try
                {
                    t.Exception!.Flatten().Handle(ex => ex is TaskCanceledException or OperationCanceledException);
                }
                catch (Exception e)
                {
                    Log.Error(e, "Filtering recipes failed");
                }
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private void FilterTask(string input, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                filteredItems = items.ToList();
                return;
            }
            var matcher = new FuzzyMatcher(input.ToLowerInvariant(), MatchMode.FuzzyParts);
            var query = items.AsParallel().Select(i => (Item: i, Score: matcher.Matches(getString(i).ToLowerInvariant())))
                .Where(t => t.Score > 0)
                .OrderByDescending(t => t.Score)
                .Select(t => t.Item);
            token.ThrowIfCancellationRequested();
            filteredItems = query.ToList();
        }
    }
    private static readonly Dictionary<uint, object> ComboData = new();

    private static SearchableComboData<T> GetComboData<T>(uint comboKey, IEnumerable<T> items, T selectedItem, Func<T, string> getString) where T : class =>
        (SearchableComboData<T>)(
            ComboData.TryGetValue(comboKey, out var data)
            ? data
            : ComboData[comboKey] = new SearchableComboData<T>(items, selectedItem, getString));

    // https://github.com/ocornut/imgui/issues/718#issuecomment-1563162222
    public static bool SearchableCombo<T>(string id, ref T selectedItem, IEnumerable<T> items, ImFontPtr selectableFont, float width, Func<T, string> getString, Func<T, string> getId, Action<T> draw) where T : class
    {
        var comboKey = ImGui.GetID(id);
        var data = GetComboData(comboKey, items, selectedItem, getString);
        data.SetItem(selectedItem);

        using var pushId = ImRaii.PushId(id);

        width = width == 0 ? ImGui.GetContentRegionAvail().X : width;
        var availableSpace = Math.Min(ImGui.GetContentRegionAvail().X, width);
        ImGui.SetNextItemWidth(availableSpace);
        var isInputTextEnterPressed = ImGui.InputText("##input", ref data.input, 256, ImGuiInputTextFlags.EnterReturnsTrue);
        var min = ImGui.GetItemRectMin();
        var size = ImGui.GetItemRectSize();
        size.X = Math.Min(size.X, availableSpace);

        var isInputTextActivated = ImGui.IsItemActivated();

        if (isInputTextActivated)
        {
            ImGui.SetNextWindowPos(min - ImGui.GetStyle().WindowPadding);
            ImGui.OpenPopup("##popup");
            data.wasTextActive = false;
        }

        using (var popup = ImRaii.Popup("##popup", ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            if (popup)
            {
                data.wasPopupActive = true;

                if (isInputTextActivated)
                {
                    ImGui.SetKeyboardFocusHere(0);
                    data.Filter();
                }
                ImGui.SetNextItemWidth(size.X);
                if (ImGui.InputText("##input_popup", ref data.input, 256))
                    data.Filter();
                var isActive = ImGui.IsItemActive();
                if (!isActive && data.wasTextActive && ImGui.IsKeyPressed(ImGuiKey.Enter))
                    isInputTextEnterPressed = true;
                data.wasTextActive = isActive;

                using (var scrollingRegion = ImRaii.Child("scrollingRegion", new Vector2(size.X, size.Y * 10), false, ImGuiWindowFlags.HorizontalScrollbar))
                {
                    T? _selectedItem = default;
                    var height = ImGui.GetTextLineHeight();
                    var r = ListClip(data.filteredItems, height, t =>
                    {
                        var name = getString(t);
                        using (var selectFont = ImRaii.PushFont(selectableFont))
                        {
                            if (ImGui.Selectable($"##{getId(t)}"))
                            {
                                _selectedItem = t;
                                return true;
                            }
                        }
                        ImGui.SameLine(0, ImGui.GetStyle().ItemSpacing.X / 2f);
                        draw(t);
                        return false;
                    });
                    if (r)
                    {
                        selectedItem = _selectedItem!;
                        data.SetItem(selectedItem);
                        ImGui.CloseCurrentPopup();
                        return true;
                    }
                }

                if (isInputTextEnterPressed || ImGui.IsKeyPressed(ImGuiKey.Escape))
                {
                    if (isInputTextEnterPressed && data.filteredItems.Count > 0)
                    {
                        selectedItem = data.filteredItems[0];
                        data.SetItem(selectedItem);
                    }
                    ImGui.CloseCurrentPopup();
                    return true;
                }
            }
            else
            {
                if (data.wasPopupActive)
                {
                    data.wasPopupActive = false;
                    data.input = getString(selectedItem);
                }
            }
        }

        return false;
    }

    private static bool ListClip<T>(IReadOnlyList<T> data, float lineHeight, Predicate<T> func)
    {
        ImGuiListClipperPtr imGuiListClipperPtr;
        unsafe
        {
            imGuiListClipperPtr = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        }
        try
        {
            imGuiListClipperPtr.Begin(data.Count, lineHeight);
            while (imGuiListClipperPtr.Step())
            {
                for (var i = imGuiListClipperPtr.DisplayStart; i <= imGuiListClipperPtr.DisplayEnd; i++)
                {
                    if (i >= data.Count)
                        return false;

                    if (i >= 0)
                    {
                        if (func(data[i]))
                            return true;
                    }
                }
            }
            return false;
        }
        finally
        {
            imGuiListClipperPtr.End();
            imGuiListClipperPtr.Destroy();
        }
    }

    public static bool IconButtonSized(FontAwesomeIcon icon, Vector2 size)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        var ret = ImGui.Button(icon.ToIconString(), size);
        return ret;
    }

    // https://gist.github.com/dougbinks/ef0962ef6ebe2cadae76c4e9f0586c69#file-imguiutils-h-L219
    private static void UnderlineLastItem(Vector4 color)
    {
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        min.Y = max.Y;
        ImGui.GetWindowDrawList().AddLine(min, max, ImGui.ColorConvertFloat4ToU32(color), 1);
    }

    // https://gist.github.com/dougbinks/ef0962ef6ebe2cadae76c4e9f0586c69#file-imguiutils-h-L228
    public static unsafe void Hyperlink(string text, string url)
    {
        ImGui.TextUnformatted(text);
        UnderlineLastItem(*ImGui.GetStyleColorVec4(ImGuiCol.Text));
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            ImGui.SetTooltip("Open in Browser");
        }
    }

    public static void AlignCentered(float width, float availWidth = default)
    {
        if (availWidth == default)
            availWidth = ImGui.GetContentRegionAvail().X;
        if (availWidth > width)
            ImGui.SetCursorPosX(ImGui.GetCursorPos().X + (availWidth - width) / 2);
    }

    public static void AlignRight(float width, float availWidth = default)
    {
        if (availWidth == default)
            availWidth = ImGui.GetContentRegionAvail().X;
        if (availWidth > width)
            ImGui.SetCursorPosX(ImGui.GetCursorPos().X + availWidth - width);
    }

    public static void AlignMiddle(Vector2 size, Vector2 availSize = default)
    {
        if (availSize == default)
            availSize = ImGui.GetContentRegionAvail();
        if (availSize.X > size.X)
            ImGui.SetCursorPosX(ImGui.GetCursorPos().X + (availSize.X - size.X) / 2);
        if (availSize.Y > size.Y)
            ImGui.SetCursorPosY(ImGui.GetCursorPos().Y + (availSize.Y - size.Y) / 2);
    }

    // https://stackoverflow.com/a/67855985
    public static void TextCentered(string text, float availWidth = default)
    {
        AlignCentered(ImGui.CalcTextSize(text).X, availWidth);
        ImGui.TextUnformatted(text);
    }

    public static void TextRight(string text, float availWidth = default)
    {
        AlignRight(ImGui.CalcTextSize(text).X, availWidth);
        ImGui.TextUnformatted(text);
    }

    public static void TextMiddleNewLine(string text, Vector2 availSize)
    {
        if (availSize == default)
            availSize = ImGui.GetContentRegionAvail();
        var c = ImGui.GetCursorPos();
        AlignMiddle(ImGui.CalcTextSize(text), availSize);
        ImGui.TextUnformatted(text);
        ImGui.SetCursorPos(c + new Vector2(0, availSize.Y + ImGui.GetStyle().ItemSpacing.Y - 1));
    }

    public static bool ButtonCentered(string text, Vector2 buttonSize = default)
    {
        var buttonWidth = buttonSize.X;
        if (buttonSize == default)
            buttonWidth = ImGui.CalcTextSize(text).X + ImGui.GetStyle().FramePadding.X * 2;
        AlignCentered(buttonWidth);
        return ImGui.Button(text, buttonSize);
    }
}
