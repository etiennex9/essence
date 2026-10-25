using Essence.UI.Services;

namespace Essence.UI.Tests;

public class CarProfilesTests
{
    private readonly MemoryPreferences _preferences = new();

    [Fact]
    public async Task Until_a_car_is_set_up_a_typical_one_follows_the_map_fuel()
    {
        var cars = new CarProfiles(_preferences);
        await cars.LoadAsync();

        await cars.SetFuelAsync("Diesel");

        Assert.True(cars.IsTypical);
        Assert.Equal(CarProfile.Typical(CarProfiles.TypicalId, "", "Diesel"), cars.Active);
        Assert.Equal((40m, 8.5m, false, 30, 10), (cars.Active.FillLitres, cars.Active.LitresPer100Km, cars.Active.CostcoMember,
            cars.Active.SpareKm, cars.Active.MaxDetourMinutes));
        Assert.Null(await _preferences.GetAsync(CarProfiles.StorageKey));
    }

    [Fact]
    public async Task Saving_the_typical_car_sets_it_up()
    {
        var cars = new CarProfiles(_preferences);

        await cars.SaveAsync(cars.Active with { Name = "Mazda3", CostcoMember = true });

        var reloaded = new CarProfiles(_preferences);
        await reloaded.LoadAsync();
        var car = Assert.Single(reloaded.Cars);
        Assert.Equal("Mazda3", car.Name);
        Assert.True(car.CostcoMember);
        Assert.NotEqual(CarProfiles.TypicalId, car.Id);
        Assert.Equal(car, reloaded.Active);
    }

    [Fact]
    public async Task Adding_a_car_keeps_the_typical_one_and_drives_the_new_one()
    {
        var cars = new CarProfiles(_preferences);
        await cars.SetFuelAsync("Super");

        var corolla = await cars.AddAsync("Corolla", firstName: "My car");

        Assert.Equal(["My car", "Corolla"], cars.Cars.Select(c => c.Name));
        Assert.Equal(corolla, cars.Active);
        Assert.Equal("Super", corolla.Fuel);

        var reloaded = new CarProfiles(_preferences);
        await reloaded.LoadAsync();
        Assert.Equal(corolla.Id, reloaded.Active.Id);
    }

    [Fact]
    public async Task Picking_a_car_is_saved()
    {
        var cars = new CarProfiles(_preferences);
        await cars.AddAsync("Corolla", "Mazda3");

        await cars.SelectAsync(cars.Cars[0].Id);

        var reloaded = new CarProfiles(_preferences);
        await reloaded.LoadAsync();
        Assert.Equal("Mazda3", reloaded.Active.Name);
    }

    [Fact]
    public async Task The_map_fuel_updates_the_car_being_driven()
    {
        var cars = new CarProfiles(_preferences);
        await cars.AddAsync("Corolla", "Mazda3");

        await cars.SetFuelAsync("Diesel");

        Assert.Equal("Diesel", cars.Active.Fuel);
        Assert.Equal(Fuels.Default, cars.Cars[0].Fuel);
    }

    [Fact]
    public async Task Deleting_the_car_being_driven_drives_the_first_one_left_then_the_typical_one()
    {
        var cars = new CarProfiles(_preferences);
        var corolla = await cars.AddAsync("Corolla", "Mazda3");
        var mazda = cars.Cars[0];

        await cars.RemoveAsync(corolla.Id);
        Assert.Equal(mazda, cars.Active);

        await cars.RemoveAsync(mazda.Id);
        Assert.True(cars.IsTypical);
        Assert.Equal(CarProfiles.TypicalId, cars.Active.Id);
    }

    [Fact]
    public async Task Settings_are_kept_in_range()
    {
        var cars = new CarProfiles(_preferences);

        await cars.SaveAsync(
            cars.Active with { Fuel = "Kerosene", FillLitres = 0, LitresPer100Km = 99, SpareKm = -5, MaxDetourMinutes = 600 });

        Assert.Equal((Fuels.Default, 1m, 40m, 0, 120), (cars.Active.Fuel, cars.Active.FillLitres, cars.Active.LitresPer100Km,
            cars.Active.SpareKm, cars.Active.MaxDetourMinutes));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"id\":\"a\"}")]
    [InlineData("[null,{\"name\":\"no id\"}]")]
    [InlineData("")]
    public async Task Unreadable_storage_starts_with_the_typical_car(string saved)
    {
        await _preferences.SetAsync(CarProfiles.StorageKey, saved);
        var cars = new CarProfiles(_preferences);

        await cars.LoadAsync();

        Assert.True(cars.IsTypical);
    }

    [Fact]
    public async Task A_missing_active_car_falls_back_to_the_first()
    {
        var cars = new CarProfiles(_preferences);
        await cars.AddAsync("Corolla", "Mazda3");
        await _preferences.SetAsync(CarProfiles.ActiveKey, "gone");

        var reloaded = new CarProfiles(_preferences);
        await reloaded.LoadAsync();

        Assert.Equal("Mazda3", reloaded.Active.Name);
    }

    [Fact]
    public async Task Changes_are_announced()
    {
        var cars = new CarProfiles(_preferences);
        var changes = 0;
        cars.Changed += () => changes++;

        await cars.SetFuelAsync("Diesel"); // the typical car's fuel
        await cars.SetFuelAsync("Diesel"); // unchanged
        await cars.SaveAsync(cars.Active with { Name = "Mazda3" });
        await cars.SaveAsync(cars.Active); // unchanged
        await cars.SelectAsync(cars.Active.Id); // already driven

        Assert.Equal(2, changes);
    }
}
