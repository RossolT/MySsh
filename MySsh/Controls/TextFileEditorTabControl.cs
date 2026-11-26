using System.Text.RegularExpressions;
using FastColoredTextBoxNS;
using MySsh.Services;

namespace MySsh.Controls;

/// <summary>Вкладка редактирования удалённого текстового файла по SFTP.</summary>
public sealed class TextFileEditorTabControl : UserControl, IDisposable
{
    private static readonly Style CommentStyle = new TextStyle(Brushes.SeaGreen, null, FontStyle.Italic);
    private static readonly Style StringStyle = new TextStyle(Brushes.Firebrick, null, FontStyle.Regular);
    private static readonly Style NumberStyle = new TextStyle(Brushes.MediumBlue, null, FontStyle.Regular);
    private static readonly Style KeyOrCommandStyle = new TextStyle(Brushes.DarkSlateBlue, null, FontStyle.Bold);

    private static readonly Regex HashCommentRegex = new(@"#[^\r\n]*", RegexOptions.Compiled);
    private static readonly Regex QuotedStringRegex = new(@"""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'", RegexOptions.Compiled);
    private static readonly Regex NumberRegex = new(@"\b\d+(?:\.\d+)?\b", RegexOptions.Compiled);
    private static readonly Regex IniKeyRegex = new(@"^[ \t]*[A-Za-z0-9_.-]+(?=[ \t]*=)", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex JsonKeyRegex = new(@"""(?:\\.|[^""\\])*""(?=\s*:)", RegexOptions.Compiled);
    private static readonly Regex ShellKeywordRegex = new(
        @"\b(?:if|then|else|elif|fi|for|while|do|done|case|esac|function|in|export|local|return|exit)\b",
        RegexOptions.Compiled);

    private readonly string _remotePath;
    private readonly string _extension;
    private readonly Func<string, CancellationToken, Task<SftpOperationResult>> _saveContentAsync;
    private readonly Action<TextFileEditorTabControl>? _closeTabAction;

    private readonly Panel _topBar = new();
    private readonly Label _lblStatus = new();
    private readonly FlatButton _btnSave = new() { Text = "Сохранить", Width = 110, Height = 28 };
    private readonly FlatButton _btnWrap = new() { Text = "Перенос: Вкл", Width = 115, Height = 28 };
    private readonly FlatButton _btnClose = new() { Text = "Закрыть", Width = 95, Height = 28 };

    private readonly FastColoredTextBox _editor = new();

    private string _lastSavedText;
    private bool _isSaving;
    private bool _disposed;

    public event EventHandler<string>? StatusChanged;
    public event EventHandler? FileSaved;

    public TextFileEditorTabControl(
        string remotePath,
        string initialContent,
        Func<string, CancellationToken, Task<SftpOperationResult>> saveContentAsync,
        Action<TextFileEditorTabControl>? closeTabAction = null)
    {
        _remotePath = remotePath;
        _extension = Path.GetExtension(remotePath).ToLowerInvariant();
        _lastSavedText = initialContent.Replace("\r\n", "\n");
        _saveContentAsync = saveContentAsync;
        _closeTabAction = closeTabAction;

        Dock = DockStyle.Fill;
        BackColor = Theme.WindowBack;
        ForeColor = Theme.TextPrimary;
        Font = new Font("Segoe UI", 9.5f);

        BuildLayout();
        ConfigureEditor(remotePath, _lastSavedText);
        WireEvents();
        ApplyCustomHighlighting();
        UpdateDirtyState();
    }

    private void BuildLayout()
    {
        _topBar.Dock = DockStyle.Top;
        _topBar.Height = 40;
        _topBar.BackColor = Theme.PanelBack;
        _topBar.Padding = new Padding(10, 6, 10, 6);

        _btnClose.Dock = DockStyle.Right;
        _btnWrap.Dock = DockStyle.Right;

        _btnSave.Dock = DockStyle.Right;
        _btnSave.NormalColor = Theme.Accent;
        _btnSave.HoverColor = Theme.AccentHover;
        _btnSave.BackColor = Theme.Accent;

        _lblStatus.Dock = DockStyle.Fill;
        _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        _lblStatus.ForeColor = Theme.TextMuted;
        _lblStatus.Text = $"Файл: {_remotePath}";

        _topBar.Controls.Add(_lblStatus);
        _topBar.Controls.Add(_btnClose);
        _topBar.Controls.Add(_btnWrap);
        _topBar.Controls.Add(_btnSave);

        _editor.Dock = DockStyle.Fill;

        Controls.Add(_editor);
        Controls.Add(_topBar);
    }

    private void ConfigureEditor(string remotePath, string normalizedContent)
    {
        _editor.Font = new Font("Consolas", 10.5f);
        _editor.BackColor = Color.White;
        _editor.ForeColor = Color.FromArgb(30, 32, 36);
        _editor.CurrentLineColor = Color.FromArgb(240, 245, 255);
        _editor.SelectionColor = Color.FromArgb(185, 215, 255);
        _editor.LineNumberColor = Color.FromArgb(120, 126, 134);
        _editor.IndentBackColor = Color.FromArgb(245, 246, 248);
        _editor.ShowLineNumbers = true;
        _editor.WordWrap = true;
        _editor.Language = DetectLanguage(_extension);
        _editor.Text = normalizedContent.Replace("\r\n", "\n");
        _editor.ClearUndo();
    }

    private static Language DetectLanguage(string ext)
    {
        return ext switch
        {
            ".cs" => Language.CSharp,
            ".js" or ".mjs" or ".cjs" => Language.JS,
            ".html" or ".htm" => Language.HTML,
            ".xml" or ".config" or ".csproj" or ".props" or ".targets" => Language.XML,
            ".sql" => Language.SQL,
            ".php" => Language.PHP,
            ".lua" => Language.Lua,
            _ => Language.Custom
        };
    }

    private void WireEvents()
    {
        _editor.TextChanged += (_, _) => UpdateDirtyState();
        _editor.TextChangedDelayed += (_, _) => ApplyCustomHighlighting();

        _editor.KeyDown += async (_, e) =>
        {
            if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.S)
            {
                e.SuppressKeyPress = true;
                await SaveChangesAsync();
            }
            else if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.F)
            {
                e.SuppressKeyPress = true;
                _editor.ShowFindDialog();
            }
        };

        _btnSave.Click += async (_, _) => await SaveChangesAsync();

        _btnWrap.Click += (_, _) =>
        {
            _editor.WordWrap = !_editor.WordWrap;
            _btnWrap.Text = _editor.WordWrap ? "Перенос: Вкл" : "Перенос: Выкл";
        };

        _btnClose.Click += (_, _) => RequestClose();
    }

    private void ApplyCustomHighlighting()
    {
        if (_disposed || _editor.IsDisposed) return;

        bool isJson = _extension == ".json";
        bool isConfig = _extension is ".ini" or ".conf" or ".env" or ".cfg" or ".properties";
        bool isShell = _extension is ".sh" or ".bash" or ".zsh";

        // Если выбран встроенный язык FCTB (C#, JS, HTML, XML, SQL, PHP, Lua), не затираем его стили.
        if (_editor.Language != Language.Custom)
            return;

        var range = _editor.Range;
        range.ClearStyle(CommentStyle, StringStyle, NumberStyle, KeyOrCommandStyle);

        if (isJson)
        {
            range.SetStyle(StringStyle, QuotedStringRegex);
            range.SetStyle(KeyOrCommandStyle, JsonKeyRegex);
            range.SetStyle(NumberStyle, NumberRegex);
            return;
        }

        if (isConfig)
        {
            range.SetStyle(KeyOrCommandStyle, IniKeyRegex);
            range.SetStyle(StringStyle, QuotedStringRegex);
            range.SetStyle(NumberStyle, NumberRegex);
            range.SetStyle(CommentStyle, HashCommentRegex);
            return;
        }

        if (isShell)
        {
            range.SetStyle(KeyOrCommandStyle, ShellKeywordRegex);
            range.SetStyle(StringStyle, QuotedStringRegex);
            range.SetStyle(NumberStyle, NumberRegex);
            range.SetStyle(CommentStyle, HashCommentRegex);
            return;
        }

        // Для прочих текстовых файлов и конфигов подсвечиваем строки, числа и комментарии '#'.
        range.SetStyle(StringStyle, QuotedStringRegex);
        range.SetStyle(NumberStyle, NumberRegex);
        range.SetStyle(CommentStyle, HashCommentRegex);
    }

    private void UpdateDirtyState()
    {
        if (_disposed) return;

        string currentNormalized = _editor.Text.Replace("\r\n", "\n");
        bool dirty = currentNormalized != _lastSavedText;

        _btnSave.Enabled = dirty && !_isSaving;
        string status = dirty
            ? $"Изменён: {_remotePath} (Ctrl+S — сохранить)"
            : $"Файл: {_remotePath} (Ctrl+S — сохранить, Ctrl+F — поиск)";

        _lblStatus.Text = status;
        StatusChanged?.Invoke(this, status);
    }

    private async Task SaveChangesAsync()
    {
        if (_disposed || _isSaving) return;

        _isSaving = true;
        _btnSave.Enabled = false;
        _btnClose.Enabled = false;
        _lblStatus.ForeColor = Theme.TextMuted;
        _lblStatus.Text = $"Сохранение «{_remotePath}»…";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string normalized = _editor.Text.Replace("\r\n", "\n");
            var res = await _saveContentAsync(normalized, cts.Token);

            if (_disposed) return;

            if (!res.Success)
            {
                _lblStatus.ForeColor = Theme.Danger;
                _lblStatus.Text = "Ошибка сохранения: " + (res.ErrorMessage ?? "неизвестная ошибка");
                MessageBox.Show(this, "Не удалось сохранить файл:\n" + res.ErrorMessage,
                    "MySsh", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _lastSavedText = normalized;
            _lblStatus.ForeColor = Theme.TextPrimary;
            _lblStatus.Text = $"Сохранено: {_remotePath}";
            FileSaved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            _lblStatus.ForeColor = Theme.Danger;
            _lblStatus.Text = "Ошибка сохранения";
            MessageBox.Show(this, "Не удалось сохранить файл:\n" + ex.Message,
                "MySsh", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _isSaving = false;
            if (!_disposed && !IsDisposed)
            {
                _btnClose.Enabled = true;
                UpdateDirtyState();
            }
        }
    }

    public bool RequestClose()
    {
        if (_isSaving)
            return false;

        string currentNormalized = _editor.Text.Replace("\r\n", "\n");
        if (currentNormalized != _lastSavedText)
        {
            var answer = MessageBox.Show(this,
                "Есть несохранённые изменения. Закрыть вкладку без сохранения?",
                "MySsh", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (answer != DialogResult.Yes)
                return false;
        }

        _closeTabAction?.Invoke(this);
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _editor.Dispose();
        }
        base.Dispose(disposing);
    }
}