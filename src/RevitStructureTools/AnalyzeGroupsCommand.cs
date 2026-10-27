using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Windows.Forms;

namespace RevitStructureTools
{
    internal sealed class GroupElement
    {
        public Element Element;
        public bool Concrete;
        public string TargetName => Concrete ? Names.ConcreteGroup : Names.RebarGroup;
    }

    internal sealed class GroupBucket
    {
        public readonly List<GroupElement> Elements = new List<GroupElement>();
        public readonly Dictionary<string, int> Votes = new Dictionary<string, int>(StringComparer.Ordinal);
        public int RebarCount => Elements.Count(item => !item.Concrete);
        public int ConcreteCount => Elements.Count(item => item.Concrete);
    }

    [Transaction(TransactionMode.Manual)]
    public class AnalyzeGroupsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try { return Run(data.Application); }
            catch (Exception error) { return Reports.Error(error, ref message); }
        }

        private static Result Run(UIApplication application)
        {
            Document document = application.ActiveUIDocument?.Document;
            if (document == null || document.IsFamilyDocument) return Result.Cancelled;
            var question = new TaskDialog("Заполнение групп бетона и арматуры")
            {
                MainInstruction = "Использовать имеющиеся значения групп в проекте?",
                MainContent = "Да — автоматически выбрать самое частое непустое значение для каждой марки среди арматуры / ОМ и бетона и записать всем её элементам, включая заполненные иначе.\n\n"
                    + "Нет — открыть пустую таблицу для ручного заполнения.\n\nОбработка всей текущей модели. При равенстве частот марка пропускается.",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No | TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Cancel
            };
            TaskDialogResult answer = question.Show();
            if (answer != TaskDialogResult.Yes && answer != TaskDialogResult.No) return Result.Cancelled;

            var buckets = new SortedDictionary<string, GroupBucket>(StringComparer.Ordinal);
            var errors = new List<string>();
            int scanned = 0, noMark = 0, nonstructuralWalls = 0, noTarget = 0;
            BuiltInCategory[] categories = { BuiltInCategory.OST_Rebar, BuiltInCategory.OST_GenericModel,
                BuiltInCategory.OST_Floors, BuiltInCategory.OST_Walls };
            foreach (BuiltInCategory category in categories)
            {
                bool concrete = category == BuiltInCategory.OST_Floors || category == BuiltInCategory.OST_Walls;
                foreach (Element element in new FilteredElementCollector(document).OfCategory(category).WhereElementIsNotElementType())
                {
                    try
                    {
                        if (category == BuiltInCategory.OST_Walls)
                        {
                            Parameter structural = element.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT);
                            if (structural == null || structural.AsInteger() != 1) { nonstructuralWalls++; continue; }
                        }
                        scanned++;
                        string mark = Parameters.GroupSourceMark(document, element, concrete);
                        if (string.IsNullOrWhiteSpace(mark)) { noMark++; continue; }
                        if (!buckets.TryGetValue(mark, out GroupBucket bucket)) buckets.Add(mark, bucket = new GroupBucket());
                        var target = new GroupElement { Element = element, Concrete = concrete };
                        bucket.Elements.Add(target);
                        Parameter parameter = Parameters.StringParameter(element, target.TargetName);
                        if (parameter == null) { noTarget++; continue; }
                        string value = parameter.AsString() ?? "";
                        // Whitespace is ignored only for emptiness. Nonempty values are counted verbatim, as in Python.
                        if (!string.IsNullOrWhiteSpace(value))
                            bucket.Votes[value] = bucket.Votes.TryGetValue(value, out int count) ? count + 1 : 1;
                    }
                    catch (Exception error) { errors.Add("ID " + element.Id.IntegerValue + ": " + error.Message); }
                }
            }
            if (buckets.Count == 0)
            {
                Reports.Show(application, "Группы — результат", $"Элементы с заполненными марками не найдены.\r\nРассмотрено: {scanned}. Без марки: {noMark}. Ошибок: {errors.Count}.", errors);
                return Result.Succeeded;
            }

            var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
            var skippedMarks = new List<string>();
            if (answer == TaskDialogResult.Yes)
            {
                foreach (var pair in buckets)
                {
                    string value = DecisionRules.ChooseGroup(pair.Value.Votes, out string reason);
                    if (value == null) skippedMarks.Add(pair.Key + ": " + reason);
                    else mapping.Add(pair.Key, value);
                }
            }
            else
            {
                using (var form = new MappingForm(buckets))
                {
                    if (form.ShowDialog(new WindowOwner(application)) != DialogResult.OK) return Result.Cancelled;
                    mapping = form.Mapping;
                }
                skippedMarks.AddRange(buckets.Keys.Where(mark => !mapping.ContainsKey(mark)).Select(mark => mark + ": не заполнено в таблице"));
            }

            int changedArm = 0, changedConcrete = 0, unchanged = 0, readOnly = 0, readOnlyNested = 0, missingWrite = 0;
            if (mapping.Count != 0)
            {
                Transactions.Run(document, "Заполнение групп бетона и арматуры", () =>
                {
                    foreach (var pair in mapping.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    {
                        foreach (GroupElement target in buckets[pair.Key].Elements)
                        {
                            try
                            {
                                Parameter parameter = Parameters.StringParameter(target.Element, target.TargetName);
                                if (parameter == null) { missingWrite++; continue; }
                                if ((parameter.AsString() ?? "") == pair.Value) { unchanged++; continue; }
                                if (parameter.IsReadOnly)
                                {
                                    if (Parameters.IsReadOnlyNested(target.Element, parameter))
                                    {
                                        readOnlyNested++;
                                        continue;
                                    }
                                    readOnly++;
                                    continue;
                                }
                                if (!parameter.Set(pair.Value)) throw new InvalidOperationException("Parameter.Set вернул False");
                                if (target.Concrete) changedConcrete++; else changedArm++;
                            }
                            catch (Exception error) { errors.Add("ID " + target.Element.Id.IntegerValue + ": " + error.Message); }
                        }
                    }
                });
            }
            var details = new List<string> { "=== СООТВЕТСТВИЯ ===" };
            details.AddRange(mapping.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
                $"{pair.Key} → {pair.Value} (арматура / ОМ: {buckets[pair.Key].RebarCount}, бетон: {buckets[pair.Key].ConcreteCount})"));
            details.Add("\r\n=== ПРОПУЩЕННЫЕ МАРКИ ===");
            details.AddRange(skippedMarks);
            details.Add("\r\n=== ОШИБКИ / НЕДОСТУПНЫЕ ПАРАМЕТРЫ ===");
            details.AddRange(errors);
            string summary = $"Рассмотрено элементов: {scanned}\r\nУникальных марок: {buckets.Count}\r\nИзменено арматуры / ОМ: {changedArm}\r\n"
                + $"Изменено перекрытий / несущих стен: {changedConcrete}\r\nУже совпадало: {unchanged}\r\nБез марки: {noMark}\r\nПропущено марок: {skippedMarks.Count}\r\n"
                + $"Нет целевого параметра при анализе: {noTarget}\r\nНет целевого параметра при записи: {missingWrite}\r\nПропущено без прямой записи: {readOnly + readOnlyNested} (вложенных: {readOnlyNested})\r\n"
                + $"Ненесущие стены исключены: {nonstructuralWalls}\r\nДиагностических сообщений: {errors.Count}";
            Reports.Show(application, "ВРС / ВРБ — результат", summary, details);
            return Result.Succeeded;
        }
    }
}
