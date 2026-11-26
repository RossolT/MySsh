using MySsh.Models;
using MySsh.Services;

namespace MySsh.Controls;

/// <summary>Одна панель двухпанельного проводника (локальная или удалённая) с мягкой сеткой.</summary>
public sealed class FilePanelControl : UserControl
{
    private static readonly Color GridLineSoft = Color.FromArgb(48, 52, 60);

    private readonly Label _titleLabel = new();
    private readonly TextBox _pathBox = new();
    private readonly FlatButton _btnUp = new() { Text = "↑", Width = 34, Height = 26 };
    private readonly FlatButton _btnRefresh = new() { Text = "↻", Width = 34, Height = 26 };
    private readonly DataGridView _grid = new();

    private List<FileEntryItem> _items = [];

    public string CurrentPath { get; private set; } = string.Empty;

    public event Func<string, Task>? NavigateRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler<FileEntryItem>? FileActivated;
    public event EventHandler? SelectionChanged;

    public FilePanelControl(string title)
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.PanelBack;
        ForeColor = Theme.TextPrimary;
        Font = new Font("Segoe UI", 9.5f);
        Padding = new Padding(6);

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 26,
            BackColor = Theme.PanelBack
        };

        _titleLabel.Dock = DockStyle.Fill;
        _titleLabel.Text = title;
        _titleLabel.ForeColor = Theme.TextMuted;
        _titleLabel.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _titleLabel.TextAlign = ContentAlignment.MiddleLeft;
        header.Controls.Add(_titleLabel);

        var navBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 32,
            BackColor = Theme.PanelBack,
            Padding = new Padding(0, 4, 0, 2)
        };

        _btnRefresh.Dock = DockStyle.Right;
        _btnRefresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);

        _btnUp.Dock = DockStyle.Right;
        _btnUp.Click += async (_, _) => await GoUpAsync();

        _pathBox.Dock = DockStyle.Fill;
        _pathBox.BackColor = Theme.InputBack;
        _pathBox.ForeColor = Theme.TextPrimary;
        _pathBox.BorderStyle = BorderStyle.FixedSingle;
        _pathBox.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                if (NavigateRequested is not null)
                    await NavigateRequested.Invoke(_pathBox.Text.Trim());
            }
        };

        navBar.Controls.Add(_pathBox);
        navBar.Controls.Add(_btnUp);
        navBar.Controls.Add(_btnRefresh);

        ConfigureGrid();

        // В WinForms Dock.Fill добавляется в Controls первым, Dock.Top — после него.
        Controls.Add(_grid);
        Controls.Add(navBar);
        Controls.Add(header);
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.MultiSelect = true;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoGenerateColumns = false;
        _grid.RowHeadersVisible = false;
        _grid.BackgroundColor = Theme.WindowBack;
        _grid.GridColor = GridLineSoft;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.Single;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.EnableHeadersVisualStyles = false;
        _grid.RowTemplate.Height = 26;

        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.PanelBack,
            ForeColor = Theme.TextMuted,
            SelectionBackColor = Theme.PanelBack,
            SelectionForeColor = Theme.TextMuted,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Padding = new Padding(6, 0, 6, 0)
        };
        _grid.ColumnHeadersHeight = 30;

        _grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.WindowBack,
            ForeColor = Theme.TextPrimary,
            SelectionBackColor = Color.FromArgb(52, 82, 128),
            SelectionForeColor = Theme.TextPrimary,
            Padding = new Padding(6, 0, 6, 0)
        };

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Имя",
            DataPropertyName = nameof(FileEntryItem.Name),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Тип",
            DataPropertyName = nameof(FileEntryItem.TypeText),
            Width = 75
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Размер",
            DataPropertyName = nameof(FileEntryItem.SizeText),
            Width = 95,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight }
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Изменён",
            DataPropertyName = nameof(FileEntryItem.ModifiedText),
            Width = 130
        });

        _grid.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        _grid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0)
                await ActivateRowAsync(e.RowIndex);
        };
        _grid.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                if (_grid.CurrentRow is { Index: >= 0 } row)
                    await ActivateRowAsync(row.Index);
            }
            else if (e.KeyCode == Keys.Back)
            {
                e.SuppressKeyPress = true;
                await GoUpAsync();
            }
        };
    }

    public void SetEntries(string path, IEnumerable<FileEntryItem> entries)
    {
        CurrentPath = path;
        _pathBox.Text = path;
        _items = entries.ToList();

        _grid.DataSource = null;
        _grid.DataSource = _items;

        for (int i = 0; i < _items.Count && i < _grid.Rows.Count; i++)
        {
            if (_items[i].IsDirectory || _items[i].IsParentLink)
                _grid.Rows[i].DefaultCellStyle.ForeColor = Theme.AccentHover;
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<FileEntryItem> SelectedEntries =>
        _grid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(r => r.DataBoundItem as FileEntryItem)
            .Where(x => x is not null && !x.IsParentLink)
            .Cast<FileEntryItem>()
            .ToList();

    private async Task GoUpAsync()
    {
        if (string.IsNullOrEmpty(CurrentPath) || NavigateRequested is null)
            return;

        if (CurrentPath.Contains('/'))
        {
            string parent = SftpService.GetRemoteParent(CurrentPath);
            await NavigateRequested.Invoke(parent);
        }
        else
        {
            var dirInfo = Directory.GetParent(CurrentPath);
            if (dirInfo is not null)
                await NavigateRequested.Invoke(dirInfo.FullName);
        }
    }

    private async Task ActivateRowAsync(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _items.Count)
            return;

        FileEntryItem entry = _items[rowIndex];
        if (entry.IsDirectory || entry.IsParentLink)
        {
            if (NavigateRequested is not null)
                await NavigateRequested.Invoke(entry.FullPath);
        }
        else
        {
            FileActivated?.Invoke(this, entry);
        }
    }
}