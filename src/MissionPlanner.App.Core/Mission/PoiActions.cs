namespace MissionPlanner.App.Core.Mission;

/// <summary>The POI menu of the DATA and PLAN maps (Mission Planner's POI submenu).</summary>
public sealed class PoiActions(AppServices services)
{
    private PoiStore Store => services.Pois;

    private static readonly FileFilter PoiFiles = new("POI files", "txt");

    public async Task AddHereAsync(double lat, double lng)
    {
        var name = await services.Dialogs.PromptAsync("Add POI", "Name of the point of interest:");
        if (name == null)
            return;
        Store.Add(lat, lng, name);
    }

    public async Task AddAtCoordsAsync()
    {
        var answer = await services.Dialogs.PromptAsync("Add POI", "Coordinates as lat;long (decimal degrees), then ;name if you like:");
        if (answer == null)
            return;
        var parts = answer.Split(';');
        var name = parts.Length >= 3 ? parts[2].Trim() : "";
        if (ViewModels.FlightData.CoordinateInput.Parse(string.Join(';', parts.Take(2))) is not { } c)
        {
            services.Notify($"\"{answer}\" is not lat;long", MessageKind.Error);
            return;
        }

        if (name.Length == 0)
            name = await services.Dialogs.PromptAsync("Add POI", "Name of the point of interest:") ?? "";
        Store.Add(c.Lat, c.Lng, name);
    }

    public async Task EditAsync(Poi poi)
    {
        var name = await services.Dialogs.PromptAsync("Edit POI", "Name of the point of interest:", poi.Name);
        if (name != null)
            Store.Rename(poi, name);
    }

    public void Delete(Poi poi) => Store.Remove(poi);

    public async Task SaveAsync()
    {
        var path = await services.Dialogs.SaveFileAsync("Save POIs", "poi.txt", PoiFiles);
        if (path == null)
            return;
        Store.SaveFile(path);
        services.Notify($"{Store.Items.Count} POIs saved");
    }

    public async Task LoadAsync()
    {
        var path = await services.Dialogs.OpenFileAsync("Load POIs", PoiFiles);
        if (path == null)
            return;
        try
        {
            var added = Store.LoadFile(path);
            services.Notify($"{added} POIs added");
        }
        catch (Exception ex)
        {
            services.Notify("Could not read the POI file: " + ex.Message, MessageKind.Error);
        }
    }
}
