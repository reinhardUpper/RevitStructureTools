using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace RevitStructureTools
{
    [Transaction(TransactionMode.Manual)]
    public class FillConstructionMarkCommand : IExternalCommand
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
            View view = document.ActiveView;
            if (!FilteredElementCollector.IsViewValidForElementIteration(document, view.Id))
            {
                TaskDialog.Show(Names.ConstructionMark, "Откройте модельный вид, на котором можно собирать элементы.");
                return Result.Cancelled;
            }
            using (var geometry = new HostGeometry(document))
            {
                var analysisTime = Stopwatch.StartNew();
                var targets = new Dictionary<int, Element>();
                var nestedIds = new HashSet<int>();
                var visited = new HashSet<int>();
                var details = new List<string>();
                ICollection<Element> visible = new FilteredElementCollector(document, view.Id).WhereElementIsNotElementType().ToElements();
                foreach (Element element in visible)
                {
                    AddTarget(element, targets);
                    if (element is FamilyInstance family)
                    {
                        geometry.WalkNested(family, visited, child =>
                        {
                            if (AddTarget(child, targets)) nestedIds.Add(child.Id.IntegerValue);
                        });
                    }
                    if (element is AreaReinforcement area)
                    {
                        try
                        {
                            foreach (ElementId id in area.GetRebarInSystemIds()) AddTarget(document.GetElement(id), targets);
                        }
                        catch (Exception error) { details.Add("ID " + area.Id.IntegerValue + ": сбор системной арматуры: " + error.Message); }
                    }
                }
                foreach (Element element in new FilteredElementCollector(document, view.Id).OfClass(typeof(RebarInSystem)).WhereElementIsNotElementType())
                    AddTarget(element, targets);

                int changed = 0, same = 0, noHost = 0, noParameter = 0, errors = 0, readOnly = 0, skippedAnalysis = 0;
                int native = 0, system = 0, areas = 0, familyRebar = 0, generic = 0, windows = 0;
                int nestedCount = 0, parentsByNested = 0, prefixV = 0, readOnlyNested = 0;
                // Analyze before starting the transaction, so geometry caches cannot become stale after parameter writes.
                var assignments = new List<KeyValuePair<Element, HostMatch>>();
                foreach (Element element in targets.Values)
                {
                    try
                    {
                        if (nestedIds.Contains(element.Id.IntegerValue)) nestedCount++;
                        Parameter targetParameter = element.LookupParameter(Names.ConstructionMark);
                        if (targetParameter != null && targetParameter.IsReadOnly)
                        {
                            readOnly++;
                            if (Parameters.IsReadOnlyNested(element, targetParameter)) readOnlyNested++;
                            continue;
                        }
                        if (geometry.Prefix(element) == "В") prefixV++;
                        if (element is AreaReinforcement) areas++;
                        else if (element is RebarInSystem) system++;
                        else if (element is Rebar) native++;
                        else if (HostGeometry.CategoryIs(element, BuiltInCategory.OST_Windows)) windows++;
                        else if (HostGeometry.IsFamilyRebar(element)) familyRebar++;
                        else
                        {
                            generic++;
                            if (geometry.NestedRebars(element).Count != 0) parentsByNested++;
                        }
                        HostMatch match = geometry.FindHost(element, out string reason, out bool noIntersection);
                        if (match == null)
                        {
                            if (noIntersection)
                            {
                                noHost++;
                                string category = element.Category?.Name ?? element.GetType().Name;
                                details.Add("ID " + element.Id.IntegerValue + " (" + category + ") | Нет пересечения со стенами, перекрытиями или фундаментными плитами.");
                            }
                            else skippedAnalysis++;
                            continue;
                        }
                        assignments.Add(new KeyValuePair<Element, HostMatch>(element, match));
                    }
                    catch (Exception error)
                    {
                        errors++;
                        details.Add("ID " + element.Id.IntegerValue + " | ОШИБКА АНАЛИЗА | " + error.Message);
                    }
                }
                analysisTime.Stop();
                var writeTime = Stopwatch.StartNew();
                if (assignments.Count != 0)
                {
                    Transactions.Run(document, Names.ConstructionMark + " — активный вид", () =>
                    {
                        foreach (var assignment in assignments)
                        {
                            Element element = assignment.Key;
                            HostMatch match = assignment.Value;
                            try
                            {
                                Parameter parameter = element.LookupParameter(Names.ConstructionMark);
                                if (parameter != null && parameter.IsReadOnly)
                                {
                                    readOnly++;
                                    if (Parameters.IsReadOnlyNested(element, parameter)) readOnlyNested++;
                                    continue;
                                }
                                string problem = parameter == null ? "Параметр не найден"
                                    : parameter.StorageType != StorageType.String ? "Параметр не строковый" : null;
                                if (problem != null)
                                {
                                    noParameter++;
                                    details.Add("ID " + element.Id.IntegerValue + " | " + problem);
                                    continue;
                                }
                                if (parameter.AsString() == match.Mark) same++;
                                else
                                {
                                    if (!parameter.Set(match.Mark)) throw new InvalidOperationException("Parameter.Set вернул False");
                                    changed++;
                                }
                                details.Add($"ID {element.Id.IntegerValue} | {match.Reason} | пересекаемая конструкция ID {match.Host.Id.IntegerValue} | марка {match.Mark}");
                            }
                            catch (Exception error)
                            {
                                errors++;
                                details.Add("ID " + element.Id.IntegerValue + " | ОШИБКА ЗАПИСИ | " + error.Message);
                            }
                        }
                    });
                }
                writeTime.Stop();
                string summary = $"Вид: {view.Name}\r\nВсего найдено целей на виде: {targets.Count}\r\nВремя анализа: {analysisTime.Elapsed.TotalSeconds:F1} с; записи: {writeTime.Elapsed.TotalSeconds:F1} с\r\nNative Rebar: {native}\r\nRebarInSystem: {system}\r\n"
                    + $"AreaReinforcement элементов: {areas}\r\nСемейств категории «Несущая арматура»: {familyRebar}\r\nОбобщённых моделей / IFC: {generic}\r\nОкон: {windows}\r\n"
                    + $"Вложенных целей: {nestedCount}\r\nРодителей с вложенной арматурой: {parentsByNested}\r\nС Арм.ПрефиксФормы = 'В': {prefixV}\r\n\r\n"
                    + $"Изменено: {changed}\r\nУже правильное значение: {same}\r\nПропущено без прямой записи: {readOnly} (вложенных: {readOnlyNested})\r\nНет геометрического пересечения: {noHost}\r\nПрочие пропуски анализа: {skippedAnalysis}\r\nПроблемы с параметром: {noParameter}\r\nОшибок: {errors}";
                Reports.Show(application, Names.ConstructionMark + " — результат", summary, details);
            }
            return Result.Succeeded;
        }

        private static bool AddTarget(Element element, IDictionary<int, Element> targets)
        {
            if (element == null || !HostGeometry.IsTarget(element) || element.LookupParameter(Names.ConstructionMark) == null) return false;
            targets[element.Id.IntegerValue] = element;
            return true;
        }
    }
}
