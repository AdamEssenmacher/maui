using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CarouselViewDetachedRefreshRepro;

public sealed record Product(string Id, string Name, string Price, Color CardColor);

public sealed class CatalogViewModel : INotifyPropertyChanged
{
	IReadOnlyList<Product> _products;
	Product? _selectedProduct;
	int _position;

	public CatalogViewModel()
	{
		_products = CreateOriginalCatalog();
		_position = 3;
		_selectedProduct = _products[_position];
	}

	public IReadOnlyList<Product> Products
	{
		get => _products;
		private set => SetProperty(ref _products, value);
	}

	public Product? SelectedProduct
	{
		get => _selectedProduct;
		set => SetProperty(ref _selectedProduct, value);
	}

	public int Position
	{
		get => _position;
		set => SetProperty(ref _position, value);
	}

	public Product? ExpectedProductAfterRefresh { get; private set; }

	public event PropertyChangedEventHandler? PropertyChanged;

	public void ResetForRun()
	{
		var original = CreateOriginalCatalog();
		ExpectedProductAfterRefresh = null;
		Products = original;
		SelectedProduct = original[3];
		Position = 3;
	}

	public void ApplyTravelFilter()
	{
		var filtered = CreateFilteredCatalog();
		var recommendation = filtered[1];

		Products = filtered;
		ExpectedProductAfterRefresh = recommendation;

		// A normal app can select the recommended item through CurrentItem without also
		// updating Position. Both CarouselView properties are two-way and are expected to
		// synchronize. Position intentionally remains at the old value (3).
		SelectedProduct = recommendation;
	}

	static Product[] CreateOriginalCatalog() =>
	[
		new("A0", "Everyday Tote", "$48", Color.FromArgb("#D9EAF7")),
		new("A1", "Trail Bottle", "$26", Color.FromArgb("#D8F3DC")),
		new("A2", "Desk Organizer", "$39", Color.FromArgb("#FDE2E4")),
		new("A3", "Weekend Duffel", "$92", Color.FromArgb("#FFF1C1")),
		new("A4", "Studio Headphones", "$128", Color.FromArgb("#E4D9F7"))
	];

	static Product[] CreateFilteredCatalog() =>
	[
		new("B0", "Travel Wallet", "$34", Color.FromArgb("#D9EAF7")),
		new("B1", "City Backpack", "$79", Color.FromArgb("#D8F3DC")),
		new("B2", "Packing Cubes", "$42", Color.FromArgb("#FDE2E4")),
		new("B3", "Carry-On Case", "$149", Color.FromArgb("#FFF1C1")),
		new("B4", "Noise Cancelling Buds", "$119", Color.FromArgb("#E4D9F7"))
	];

	bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(storage, value))
			return false;

		storage = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		return true;
	}
}
