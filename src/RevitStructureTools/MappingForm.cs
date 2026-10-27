using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace RevitStructureTools
{
    internal sealed class MappingForm : Form
    {
        private readonly DataGridView grid;
        public Dictionary<string, string> Mapping { get; private set; }

        public MappingForm(SortedDictionary<string, GroupBucket> buckets)
        {
            Text = "Соответствия марок и групп";
            Size = new Size(1150, 700);
            MinimumSize = new Size(800, 450);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9);
            AutoScaleMode = AutoScaleMode.Dpi;
            ShowInTaskbar = false;
            grid = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.CellSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = SystemColors.Window
            };
            string[] titles = { "Марка", "Арматура / ОМ", "Бетон", "Всего", "Значение группы (введите)" };
            int[] weights = { 28, 14, 14, 10, 34 };
            for (int i = 0; i < titles.Length; i++)
                grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = titles[i], FillWeight = weights[i], ReadOnly = i != 4,
                    SortMode = DataGridViewColumnSortMode.NotSortable
                });
            foreach (var pair in buckets)
                grid.Rows.Add(pair.Key, pair.Value.RebarCount, pair.Value.ConcreteCount, pair.Value.Elements.Count, "");
            var label = new Label
            {
                Dock = DockStyle.Top, Height = 82, Padding = new Padding(8),
                Text = "Арматура / ОМ: Мрк.МаркаКонструкции → Арм.Группа ВРС\r\n"
                    + "Бетон: Марка → Орг.ГруппаВедомостьРасходаБетона (перекрытия и несущие стены).\r\n"
                    + "Одна строка на совпадающую марку в обеих категориях. Пусто — пропустить; заполнено — записать всем."
            };
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8) };
            var apply = new Button { Text = "Применить", Width = 145 };
            var cancel = new Button { Text = "Отмена", Width = 120, DialogResult = DialogResult.Cancel };
            apply.Click += Apply;
            bottom.Controls.Add(apply);
            bottom.Controls.Add(cancel);
            Controls.Add(grid);
            Controls.Add(label);
            Controls.Add(bottom);
            CancelButton = cancel;
            // No AcceptButton: Enter remains available while editing table cells.
        }

        private void Apply(object sender, EventArgs args)
        {
            if (!grid.EndEdit()) return;
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (DataGridViewRow row in grid.Rows)
            {
                string value = Convert.ToString(row.Cells[4].Value);
                if (!string.IsNullOrWhiteSpace(value)) result.Add(Convert.ToString(row.Cells[0].Value), value);
            }
            Mapping = result;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
