using System.Reflection;
using MySsh.Controls;

namespace MySsh.Forms;

public sealed class MainForm : Form
{
    private readonly TabControl _tabs = new();

    public MainForm()
    {
        Text = "MySsh — SSH / SFTP клиент";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 600);
        Size = new Size(1200, 760);
        Font = new Font("Segoe UI", 10f);
        BackColor = Theme.WindowBack;

        var appIcon = LoadAppIcon();
        if (appIcon is not null)
            Icon = appIcon;

        _tabs.Dock = DockStyle.Fill;
        _tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        _tabs.Padding = new Point(16, 6);
        _tabs.DrawItem += Tabs_DrawItem;
        _tabs.MouseDown += Tabs_MouseDown;

        Controls.Add(_tabs);

        // Вкладка №0 — менеджер подключений, закрыть нельзя.
        var manager = new ConnectionsManagerControl(this);
        var tab = new TabPage("Подключения") { BackColor = Theme.WindowBack };
        manager.Dock = DockStyle.Fill;
        tab.Controls.Add(manager);
        _tabs.TabPages.Add(tab);
    }

    private static Icon? LoadAppIcon()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string? resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("icon.ico", StringComparison.OrdinalIgnoreCase));

        if (resourceName is not null)
        {
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is not null)
                return new Icon(stream);
        }

        string filePath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
        return File.Exists(filePath) ? new Icon(filePath) : null;
    }

    /// <summary>Запрещаем закрытие первой вкладки.</summary>
    public bool IsManagerTab(int index) => index == 0;

    /// <summary>Открыть вкладку сессии или редактора.</summary>
    public void AddSessionTab(TabPage page)
    {
        _tabs.TabPages.Add(page);
        _tabs.SelectedTab = page;
    }

    /// <summary>Закрыть конкретную вкладку, если она открыта в _tabs.</summary>
    public void CloseTabPage(TabPage page)
    {
        int index = _tabs.TabPages.IndexOf(page);
        if (index > 0)
            CloseTab(index);
    }

    // --- Отрисовка вкладок (крестик только у сессий) ---

    private void Tabs_DrawItem(object? sender, DrawItemEventArgs e)
    {
        var page = _tabs.TabPages[e.Index];
        bool selected = _tabs.SelectedIndex == e.Index;

        Color back = selected ? Theme.TabActive : Theme.TabInactive;
        Color fore = selected ? Theme.TextPrimary : Theme.TextMuted;

        using (var brush = new SolidBrush(back))
            e.Graphics.FillRectangle(brush, e.Bounds);

        int textRight = IsManagerTab(e.Index)
            ? e.Bounds.Right - 10
            : CloseButtonRect(e.Bounds).Left - 6;

        int textWidth = Math.Max(20, textRight - (e.Bounds.X + 10));

        TextRenderer.DrawText(
            e.Graphics, page.Text, Font,
            new Rectangle(e.Bounds.X + 10, e.Bounds.Y, textWidth, e.Bounds.Height),
            fore,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        if (!IsManagerTab(e.Index))
        {
            Rectangle x = CloseButtonRect(e.Bounds);
            using var pen = new Pen(fore, 1.6f);
            int p = 5;
            e.Graphics.DrawLine(pen, x.Left + p, x.Top + p, x.Right - p, x.Bottom - p);
            e.Graphics.DrawLine(pen, x.Right - p, x.Top + p, x.Left + p, x.Bottom - p);
        }
    }

    private void Tabs_MouseDown(object? sender, MouseEventArgs e)
    {
        for (int i = 0; i < _tabs.TabCount; i++)
        {
            if (!_tabs.GetTabRect(i).Contains(e.Location))
                continue;

            // Закрытие по крестику — только для сессий.
            if (!IsManagerTab(i) && CloseButtonRect(_tabs.GetTabRect(i)).Contains(e.Location))
            {
                var page = _tabs.TabPages[i];
                if (page.Controls.Count > 0 && page.Controls[0] is TextFileEditorTabControl editor)
                {
                    // Редактор сам покажет запрос при несохранённых изменениях и закроет вкладку через коллбэк.
                    editor.RequestClose();
                    return;
                }

                CloseTab(i);
            }

            return;
        }
    }

    /// <summary>Закрыть вкладку сессии и освободить ресурсы соединения.</summary>
    private void CloseTab(int index)
    {
        var page = _tabs.TabPages[index];

        if (page.Controls.Count > 0 && page.Controls[0] is IDisposable disposable)
            disposable.Dispose();

        _tabs.TabPages.Remove(page);
        page.Dispose();
    }

    private static Rectangle CloseButtonRect(Rectangle tab)
    {
        const int size = 16;
        int x = tab.Right - size - 6;
        int y = tab.Top + (tab.Height - size) / 2;
        int h = tab.Height - 8;
        return new Rectangle(x, tab.Top + 4, size, h > 0 ? h : size);
    }
}