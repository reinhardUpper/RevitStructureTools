using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Form = System.Windows.Forms.Form;
using TextBox = System.Windows.Forms.TextBox;

namespace RevitStructureTools
{
    internal static class Names
    {
        public const string Mark = "Марка";
        public const string ConstructionMark = "Мрк.МаркаКонструкции";
        public const string Prefix = "Арм.ПрефиксФормы";
        public const string RebarGroup = "Арм.Группа ВРС";
        public const string ConcreteGroup = "Орг.ГруппаВедомостьРасходаБетона";
    }

    internal static class Parameters
    {
        public static bool IsReadOnlyNested(Element element, Parameter parameter)
        {
            return parameter != null && parameter.IsReadOnly
                && element is FamilyInstance family && family.SuperComponent != null;
        }

        public static Parameter StringParameter(Element element, string name)
        {
            IList<Parameter> parameters = element.GetParameters(name);
            if (parameters.Count > 1) throw new InvalidOperationException("Несколько параметров с именем '" + name + "'");
            Parameter parameter = parameters.FirstOrDefault();
            if (parameter != null && parameter.StorageType != StorageType.String)
                throw new InvalidOperationException("Параметр '" + name + "' не текстовый");
            return parameter;
        }

        public static string Value(Parameter parameter)
        {
            if (parameter == null || !parameter.HasValue) return null;
            string value = parameter.StorageType == StorageType.String ? parameter.AsString() : parameter.AsValueString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        public static string ReadWithType(Document document, Element element, string name)
        {
            string value = Value(element.LookupParameter(name));
            if (value != null) return value;
            Element type = document.GetElement(element.GetTypeId());
            return type == null ? null : Value(type.LookupParameter(name));
        }

        public static string GroupSourceMark(Document document, Element element, bool concrete)
        {
            string name = concrete ? Names.Mark : Names.ConstructionMark;
            Parameter parameter = concrete ? element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK) : null;
            if (parameter == null) parameter = StringParameter(element, name);
            string value = parameter == null ? "" : parameter.AsString() ?? "";
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            Element type = document.GetElement(element.GetTypeId());
            parameter = type == null ? null : StringParameter(type, name);
            return parameter == null ? "" : (parameter.AsString() ?? "").Trim();
        }
    }

    internal static class Transactions
    {
        public static void Run(Document document, string name, Action action)
        {
            using (var transaction = new Transaction(document, name))
            {
                try
                {
                    if (transaction.Start() != TransactionStatus.Started)
                        throw new InvalidOperationException("Не удалось открыть транзакцию");
                    FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
                    options.SetForcedModalHandling(true);
                    transaction.SetFailureHandlingOptions(options);
                    action();
                    TransactionStatus status = transaction.Commit();
                    if (status != TransactionStatus.Committed)
                        throw new InvalidOperationException("Изменения не подтверждены Revit: " + status);
                }
                catch
                {
                    if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
                    throw;
                }
            }
        }
    }

    internal sealed class WindowOwner : IWin32Window
    {
        public IntPtr Handle { get; private set; }
        public WindowOwner(UIApplication application) { Handle = application.MainWindowHandle; }
    }

    internal static class Reports
    {
        public static void Show(UIApplication application, string title, string summary, IEnumerable<string> details)
        {
            string body = title + Environment.NewLine + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                + Environment.NewLine + Environment.NewLine + summary + Environment.NewLine + Environment.NewLine
                + string.Join(Environment.NewLine, details);
            using (var form = new Form
            {
                Text = title, Size = new Size(950, 680), MinimumSize = new Size(650, 450),
                StartPosition = FormStartPosition.CenterParent, Font = new Font("Segoe UI", 9),
                AutoScaleMode = AutoScaleMode.Dpi, ShowInTaskbar = false
            })
            {
                var box = new TextBox
                {
                    Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
                    WordWrap = false, Dock = DockStyle.Fill, Text = body, BackColor = SystemColors.Window
                };
                var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8) };
                var save = new Button { Text = "Сохранить отчёт…", AutoSize = true };
                var close = new Button { Text = "Закрыть", AutoSize = true, DialogResult = DialogResult.OK };
                save.Click += (sender, args) =>
                {
                    using (var dialog = new SaveFileDialog
                    {
                        Filter = "Текстовый отчёт (*.txt)|*.txt", FileName = "RevitStructureTools_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt"
                    })
                    {
                        if (dialog.ShowDialog(form) != DialogResult.OK) return;
                        try { File.WriteAllText(dialog.FileName, body, new UTF8Encoding(true)); }
                        catch (Exception error) { MessageBox.Show(form, error.Message, "Не удалось сохранить отчёт"); }
                    }
                };
                bottom.Controls.Add(save);
                bottom.Controls.Add(close);
                form.Controls.Add(box);
                form.Controls.Add(bottom);
                form.CancelButton = close;
                form.ShowDialog(new WindowOwner(application));
            }
        }

        public static Result Error(Exception error, ref string message)
        {
            message = error.ToString();
            TaskDialog.Show("Марки и ведомости — ошибка", error.Message);
            return Result.Failed;
        }
    }
}
