using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace RevitStructureTools
{
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            const string tab = "Марки конструкций";
            const string panelName = "Марки и ведомости";
            try
            {
                application.CreateRibbonTab(tab);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { /* The tab already exists. */ }

            RibbonPanel panel = application.GetRibbonPanels(tab).FirstOrDefault(p => p.Name == panelName)
                ?? application.CreateRibbonPanel(tab, panelName);
            Add<FillConstructionMarkCommand>(panel, "RST_FillMark", "Заполнить\nМрк.МаркаКонструкции",
                "Активный вид: заполнить Мрк.МаркаКонструкции по Марке стены, перекрытия или фундаментной плиты.", 0);
            Add<AnalyzeGroupsCommand>(panel, "RST_AnalyzeGroups", "Анализ\nВРС / ВРБ",
                "Вся модель: заполнить группы ВРС / ВРБ по наиболее частому значению или через таблицу соответствий.", 1);
            Add<ClearConstructionMarkCommand>(panel, "RST_ClearMark", "Очистить\nМрк.МаркаКонструкции",
                "Вся модель: очистить текстовый параметр Мрк.МаркаКонструкции у доступных экземпляров.", 2);
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application) { return Result.Succeeded; }

        private static void Add<T>(RibbonPanel panel, string id, string label, string tooltip, int icon)
        {
            var data = new PushButtonData(id, label, Assembly.GetExecutingAssembly().Location, typeof(T).FullName)
            {
                ToolTip = tooltip,
                AvailabilityClassName = typeof(ProjectAvailability).FullName,
                LargeImage = MakeIcon(icon),
                Image = MakeIcon(icon)
            };
            panel.AddItem(data);
        }

        // Small vector icons stay sharp at different display scales; no external images are required.
        private static DrawingImage MakeIcon(int kind)
        {
            var group = new DrawingGroup();
            using (DrawingContext c = group.Open())
            {
                c.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 32, 32));
                var blue = new SolidColorBrush(Color.FromRgb(45, 105, 160));
                var pen = new Pen(blue, 2);
                if (kind == 0)
                {
                    c.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(227, 239, 249)), pen, new Rect(3, 4, 21, 25), 2, 2);
                    c.DrawLine(pen, new Point(7, 11), new Point(19, 11));
                    c.DrawLine(pen, new Point(7, 16), new Point(16, 16));
                    var green = new Pen(Brushes.ForestGreen, 3);
                    c.DrawLine(green, new Point(14, 23), new Point(20, 28));
                    c.DrawLine(green, new Point(20, 28), new Point(30, 16));
                }
                else if (kind == 1)
                {
                    c.DrawLine(pen, new Point(3, 28), new Point(29, 28));
                    c.DrawRectangle(blue, null, new Rect(5, 16, 5, 10));
                    c.DrawRectangle(Brushes.SeaGreen, null, new Rect(13, 9, 5, 17));
                    c.DrawRectangle(Brushes.DarkOrange, null, new Rect(21, 3, 5, 23));
                }
                else
                {
                    c.DrawRoundedRectangle(Brushes.WhiteSmoke, pen, new Rect(3, 4, 21, 25), 2, 2);
                    var red = new Pen(Brushes.Firebrick, 3);
                    c.DrawLine(red, new Point(12, 15), new Point(29, 29));
                    c.DrawLine(red, new Point(12, 29), new Point(29, 15));
                }
            }
            var image = new DrawingImage(group);
            image.Freeze();
            return image;
        }
    }

    public class ProjectAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication application, CategorySet categories)
        {
            // Availability runs in a restricted UI callback. IsReadOnly describes the
            // current API context and must not be used to predict whether Execute can
            // start a transaction. Revit enforces write access when the command runs.
            UIDocument document = application.ActiveUIDocument;
            return document != null && !document.Document.IsFamilyDocument;
        }
    }
}
