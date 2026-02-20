using Microsoft.Extensions.Logging;

namespace DataWarehouse.GUI.Services;

/// <summary>
/// Service for handling application navigation.
/// This is a UI-only service - no business logic.
/// </summary>
public sealed class NavigationService
{
    private readonly ILogger<NavigationService> _logger;

    /// <summary>
    /// Initializes a new instance of the NavigationService class.
    /// </summary>
    /// <param name="logger">Logger for navigation operations.</param>
    public NavigationService(ILogger<NavigationService> logger)
    {
        _logger = logger;
    }

    private static Page? GetMainPage() => Application.Current?.Windows.FirstOrDefault()?.Page;

    /// <summary>
    /// Navigates to a new page.
    /// </summary>
    /// <param name="page">The page to navigate to.</param>
    /// <param name="animated">Whether to animate the navigation.</param>
    public async Task NavigateToAsync(Page page, bool animated = true)
    {
        try
        {
            if (GetMainPage()?.Navigation != null)
            {
                await GetMainPage()!.Navigation.PushAsync(page, animated);
                _logger.LogDebug("Navigated to {PageType}", page.GetType().Name);
            }
            else
            {
                _logger.LogWarning("Cannot navigate: Navigation is null");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to navigate to {PageType}", page.GetType().Name);
        }
    }

    /// <summary>
    /// Navigates back to the previous page.
    /// </summary>
    /// <param name="animated">Whether to animate the navigation.</param>
    /// <returns>The page that was popped from the navigation stack.</returns>
    public async Task<Page?> GoBackAsync(bool animated = true)
    {
        try
        {
            if (GetMainPage()?.Navigation != null)
            {
                var page = await GetMainPage()!.Navigation.PopAsync(animated);
                _logger.LogDebug("Navigated back from {PageType}", page?.GetType().Name ?? "unknown");
                return page;
            }
            else
            {
                _logger.LogWarning("Cannot navigate back: Navigation is null");
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to navigate back");
            return null;
        }
    }

    /// <summary>
    /// Navigates to the root page, clearing the navigation stack.
    /// </summary>
    /// <param name="animated">Whether to animate the navigation.</param>
    public async Task NavigateToRootAsync(bool animated = true)
    {
        try
        {
            if (GetMainPage()?.Navigation != null)
            {
                await GetMainPage()!.Navigation.PopToRootAsync(animated);
                _logger.LogDebug("Navigated to root");
            }
            else
            {
                _logger.LogWarning("Cannot navigate to root: Navigation is null");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to navigate to root");
        }
    }

    /// <summary>
    /// Presents a page modally.
    /// </summary>
    /// <param name="page">The page to present modally.</param>
    /// <param name="animated">Whether to animate the presentation.</param>
    public async Task PresentModalAsync(Page page, bool animated = true)
    {
        try
        {
            if (GetMainPage()?.Navigation != null)
            {
                await GetMainPage()!.Navigation.PushModalAsync(page, animated);
                _logger.LogDebug("Presented modal {PageType}", page.GetType().Name);
            }
            else
            {
                _logger.LogWarning("Cannot present modal: Navigation is null");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to present modal {PageType}", page.GetType().Name);
        }
    }

    /// <summary>
    /// Dismisses the current modal page.
    /// </summary>
    /// <param name="animated">Whether to animate the dismissal.</param>
    /// <returns>The page that was dismissed.</returns>
    public async Task<Page?> DismissModalAsync(bool animated = true)
    {
        try
        {
            if (GetMainPage()?.Navigation != null)
            {
                var page = await GetMainPage()!.Navigation.PopModalAsync(animated);
                _logger.LogDebug("Dismissed modal {PageType}", page?.GetType().Name ?? "unknown");
                return page;
            }
            else
            {
                _logger.LogWarning("Cannot dismiss modal: Navigation is null");
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dismiss modal");
            return null;
        }
    }

    /// <summary>
    /// Gets the current navigation stack.
    /// </summary>
    /// <returns>List of pages in the navigation stack.</returns>
    public IReadOnlyList<Page> GetNavigationStack()
    {
        try
        {
            if (GetMainPage()?.Navigation != null)
            {
                return GetMainPage()!.Navigation.NavigationStack;
            }
            else
            {
                _logger.LogWarning("Cannot get navigation stack: Navigation is null");
                return Array.Empty<Page>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get navigation stack");
            return Array.Empty<Page>();
        }
    }

    /// <summary>
    /// Gets the modal navigation stack.
    /// </summary>
    /// <returns>List of pages in the modal navigation stack.</returns>
    public IReadOnlyList<Page> GetModalStack()
    {
        try
        {
            if (GetMainPage()?.Navigation != null)
            {
                return GetMainPage()!.Navigation.ModalStack;
            }
            else
            {
                _logger.LogWarning("Cannot get modal stack: Navigation is null");
                return Array.Empty<Page>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get modal stack");
            return Array.Empty<Page>();
        }
    }
}
