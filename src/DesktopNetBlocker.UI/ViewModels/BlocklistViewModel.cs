using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DesktopNetBlocker.Core.Models;
using DesktopNetBlocker.Core.Network;
using DesktopNetBlocker.UI.Services;

namespace DesktopNetBlocker.UI.ViewModels;

public class BlocklistViewModel : ViewModelBase
{
    private readonly ServiceClient _serviceClient;
    private List<BlockedDomain> _allDomains = [];

    public ObservableCollection<BlockedDomain> FilteredDomains { get; } = [];

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                ApplyFilter();
        }
    }

    private string _selectedCategory = "All";
    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
                ApplyFilter();
        }
    }

    public ObservableCollection<string> Categories { get; } = ["All", "General", "Social Media", "Video & Streaming", "Gaming", "News", "Custom"];

    // Add Domain Fields
    private string _domainInputText = string.Empty;
    public string DomainInputText
    {
        get => _domainInputText;
        set => SetProperty(ref _domainInputText, value);
    }

    private MatchingMode _selectedMatchingMode = MatchingMode.Subdomain;
    public MatchingMode SelectedMatchingMode
    {
        get => _selectedMatchingMode;
        set => SetProperty(ref _selectedMatchingMode, value);
    }

    private string _newDomainCategory = "General";
    public string NewDomainCategory
    {
        get => _newDomainCategory;
        set => SetProperty(ref _newDomainCategory, value);
    }

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public ICommand AddDomainCommand { get; }
    public ICommand RequestRemovalCommand { get; }
    public ICommand RequestTemporaryUnlockCommand { get; }
    public ICommand RefreshCommand { get; }

    public BlocklistViewModel(ServiceClient serviceClient)
    {
        _serviceClient = serviceClient;

        AddDomainCommand = new RelayCommand(async () => await AddDomainsAsync());
        RequestRemovalCommand = new RelayCommand(async (param) => await RequestRemovalAsync(param as string));
        RequestTemporaryUnlockCommand = new RelayCommand(async (param) => await RequestTemporaryUnlockAsync(param as string));
        RefreshCommand = new RelayCommand(async () => await LoadDomainsAsync());
    }

    public async Task LoadDomainsAsync()
    {
        _allDomains = await _serviceClient.GetDomainsAsync();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        FilteredDomains.Clear();
        var query = _allDomains.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string search = SearchText.Trim().ToLowerInvariant();
            query = query.Where(d => d.Domain.Contains(search) || d.Category.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        if (SelectedCategory != "All")
        {
            query = query.Where(d => string.Equals(d.Category, SelectedCategory, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in query)
        {
            FilteredDomains.Add(item);
        }
    }

    private async Task AddDomainsAsync()
    {
        if (string.IsNullOrWhiteSpace(DomainInputText))
        {
            StatusMessage = "Please enter one or more domains to block.";
            return;
        }

        var (validDomains, errors) = DomainValidator.ParseBatch(DomainInputText);

        if (validDomains.Count == 0)
        {
            StatusMessage = errors.Count > 0 ? string.Join("; ", errors) : "No valid domain names found.";
            return;
        }

        var (success, msg) = await _serviceClient.AddDomainsAsync(validDomains, SelectedMatchingMode, NewDomainCategory);
        if (success)
        {
            StatusMessage = msg;
            DomainInputText = string.Empty;
            await LoadDomainsAsync();
        }
        else
        {
            StatusMessage = $"Failed to add domains: {msg}";
        }
    }

    private async Task RequestRemovalAsync(string? domainId)
    {
        if (string.IsNullOrEmpty(domainId)) return;

        var domain = _allDomains.FirstOrDefault(d => d.Id == domainId);
        if (domain == null) return;

        var result = MessageBox.Show(
            $"Are you sure you want to request removal of '{domain.Domain}'?\n\n" +
            "This will initiate a 3-day waiting countdown followed by mandatory confirmation and an additional 3-day delay. " +
            "The domain will remain BLOCKED during the entire process.",
            "Confirm Delayed Removal Request",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        var (success, message) = await _serviceClient.SubmitRemovalAsync(domainId);
        if (success)
        {
            MessageBox.Show(
                $"Removal request submitted for '{domain.Domain}'.\n\nThe 3-day waiting timer is now active.",
                "Removal Request Active",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            await LoadDomainsAsync();
        }
        else
        {
            MessageBox.Show($"Cannot submit removal request:\n\n{message}", "Request Rejected", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RequestTemporaryUnlockAsync(string? domainId)
    {
        if (string.IsNullOrEmpty(domainId)) return;

        var domain = _allDomains.FirstOrDefault(d => d.Id == domainId);
        if (domain == null) return;

        var result = MessageBox.Show(
            $"Request a temporary 2-hour access unlock for '{domain.Domain}'?\n\n" +
            "This requires the full delayed authorization workflow (3 days initial wait + confirmation + 3 days final delay). " +
            "After authorization, access will be granted for 2 hours before the block automatically resumes.",
            "Confirm Temporary Unlock Request",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        var (success, message) = await _serviceClient.SubmitTemporaryUnlockAsync(domainId);
        if (success)
        {
            MessageBox.Show(
                $"Temporary unlock request submitted for '{domain.Domain}'.\n\nThe waiting timer is now active.",
                "Unlock Request Active",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            await LoadDomainsAsync();
        }
        else
        {
            MessageBox.Show($"Cannot submit temporary unlock request:\n\n{message}", "Request Rejected", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

