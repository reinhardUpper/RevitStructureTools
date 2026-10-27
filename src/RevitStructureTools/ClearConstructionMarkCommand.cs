using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitStructureTools
{
    [Transaction(TransactionMode.Manual)]
    public class ClearConstructionMarkCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                UIApplication application = data.Application;
                Document document = application.ActiveUIDocument?.Document;
                if (document == null || document.IsFamilyDocument) return Result.Cancelled;
                ICollection<Element> targets = new FilteredElementCollector(document).WhereElementIsNotElementType().ToElements();
                int cleared = 0, empty = 0, skipped = 0, errors = 0;
                var details = new List<string>();
                Transactions.Run(document, "Очистить " + Names.ConstructionMark, () =>
                {
                    foreach (Element element in targets)
                    {
                        try
                        {
                            Parameter parameter = element.LookupParameter(Names.ConstructionMark);
                            if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.String)
                            { skipped++; continue; }
                            if (string.IsNullOrEmpty(parameter.AsString())) { empty++; continue; }
                            if (!parameter.Set("")) throw new InvalidOperationException("Parameter.Set вернул False");
                            cleared++;
                        }
                        catch (Exception error) { errors++; details.Add("ID " + element.Id.IntegerValue + ": " + error.Message); }
                    }
                });
                string summary = $"Всего элементов: {targets.Count}\r\nОчищено параметров: {cleared}\r\nУже были пустыми: {empty}\r\nПропущено: {skipped}\r\nОшибок: {errors}";
                Reports.Show(application, "Очистка " + Names.ConstructionMark + " — результат", summary, details);
                return Result.Succeeded;
            }
            catch (Exception error) { return Reports.Error(error, ref message); }
        }
    }
}
