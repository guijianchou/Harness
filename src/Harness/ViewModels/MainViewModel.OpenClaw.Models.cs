using Harness.Helpers;
using Harness.OpenClaw;
using Harness.Services;

namespace Harness.ViewModels;

public partial class MainViewModel
{
    private sealed record GatewayModelContext(OpenClawGatewayClient Client, long Connection, string? Agent,
        string Key, string? SessionId, string Model, string ActiveModel, string? ThinkingDefault);

    private GatewayModelContext? _gatewayModelContext;
    private OpenClawModelList? _gatewayModelCatalog;
    private OpenClawModelList? _gatewayProjectedModelCatalog;
    private long _gatewayModelVersion;
    private DateTimeOffset _gatewayModelsExpireAt;
    private int _gatewayModelFailures;
    private DateTimeOffset _gatewayModelRetryAt;
    private DateTimeOffset _gatewayModelRecoveryNotBefore;
    private bool _gatewayModelRetryable;
    private string? _gatewayModelErrorText;
    private string _gatewayServerModelId = "";
    private string _gatewayServerThinkingId = "";
    private string _gatewayCurrentModelNotice = "";

    public string GatewayCurrentModelNotice
    {
        get => _gatewayCurrentModelNotice;
        private set
        {
            if (SetProperty(ref _gatewayCurrentModelNotice, value))
            {
                OnPropertyChanged(nameof(HasGatewayCurrentModelNotice));
            }
        }
    }
    public bool HasGatewayCurrentModelNotice => !string.IsNullOrEmpty(GatewayCurrentModelNotice);

    private bool HasGatewayModelChanges =>
        (_gatewayModelEdited && SelectedGatewayModel?.Id != _gatewayServerModelId) ||
        (_gatewayThinkingEdited && SelectedGatewayThinkingLevel?.Id != _gatewayServerThinkingId);

    private bool IsGatewayModelChoiceAllowed(OpenClawModelItem? model, OpenClawThinkingItem? thinking) =>
        model is not null && GatewayModels.Contains(model) &&
        (model.Id.Length == 0 || model.Model is { ManualSelectionAllowed: true, Available: not false }) &&
        thinking is not null && GatewayThinkingLevels.Contains(thinking) &&
        (thinking.Id.Length == 0 || OpenClawModelSelection.ThinkingOptions(model.Model).Any(option => option.Id == thinking.Id));

    private bool IsGatewayModelCatalogFresh => _gatewayModelCatalog is not null &&
        DateTimeOffset.UtcNow < _gatewayModelsExpireAt &&
        ReferenceEquals(_gatewayModelContext?.Client, _gatewayRuntime?.Client) &&
        _gatewayModelContext?.Connection == _gatewayRuntime?.ConnectionGeneration &&
        _gatewayModelContext?.Key == _gatewayPage?.SessionKey && _gatewayModelContext?.SessionId == _gatewayHistory?.SessionId;

    private bool CanRefreshGatewayModels(DateTimeOffset now) =>
        _gatewayModelFailures == 0 || (AutoRefreshEnabled && now >= _gatewayModelRetryAt);

    private void InvalidateGatewayModels(bool clear = false, bool manual = false)
    {
        _gatewayModelVersion++;
        _gatewayModelCatalog = null;
        if (clear || manual || (_gatewayModelRetryable && _gatewayModelFailures > 3 && DateTimeOffset.UtcNow >= _gatewayModelRecoveryNotBefore))
        {
            _gatewayModelFailures = 0;
            _gatewayModelRetryAt = default;
        }
        if (clear)
        {
            _gatewayModelContext = null;
            _gatewayProjectedModelCatalog = null;
            GatewayModels.Clear();
            _selectedGatewayModel = null;
            _gatewayModelErrorText = null;
            GatewayCurrentModelNotice = "";
        }
        NotifyGatewayCapabilities();
    }

    private async Task RefreshGatewayModelsAsync(OpenClawSessionRuntime runtime, OpenClawGatewayClient client,
        string? agentId, string pageKey, string? sessionId, OpenClawSession? row, Func<bool> current, CancellationToken token)
    {
        var effective = ModelIdentifier(row?.Model, row?.ModelProvider);
        var context = new GatewayModelContext(client, runtime.ConnectionGeneration, agentId, pageKey, sessionId,
            effective, ModelIdentifier(row?.ActiveModel, row?.ActiveModelProvider), row?.ThinkingDefault);
        if (_gatewayModelContext != context)
        {
            InvalidateGatewayModels(manual: true);
            _gatewayModelContext = context;
        }
        if (!IsGatewayModelCatalogFresh)
        {
            NotifyGatewayCapabilities();
            if (!CanRefreshGatewayModels(DateTimeOffset.UtcNow))
            {
                return;
            }
            var version = _gatewayModelVersion;
            try
            {
                var models = await client.ListModelsAsync(agentId, pageKey, token);
                if (!current() || version != _gatewayModelVersion || context != _gatewayModelContext)
                {
                    return;
                }
                _gatewayModelCatalog = models;
                _gatewayModelsExpireAt = DateTimeOffset.UtcNow.AddMinutes(5);
                _gatewayModelFailures = 0;
                _gatewayModelRetryAt = default;
                _gatewayModelRetryable = false;
                if (GatewayErrorText == _gatewayModelErrorText)
                {
                    GatewayErrorText = "";
                }
                _gatewayModelErrorText = null;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (current() && version == _gatewayModelVersion && context == _gatewayModelContext)
                {
                    _gatewayModelCatalog = null;
                    _gatewayModelRetryable = ex is OpenClawGatewayException gateway &&
                        (gateway.Retryable || gateway.Code == "UNAVAILABLE" ||
                         gateway.Kind is OpenClawFailureKind.Timeout or OpenClawFailureKind.Transport or OpenClawFailureKind.Disconnected);
                    _gatewayModelFailures++;
                    _gatewayModelRecoveryNotBefore = DateTimeOffset.UtcNow.AddSeconds(8);
                    _gatewayModelRetryAt = _gatewayModelRetryable && _gatewayModelFailures <= 3 ?
                        DateTimeOffset.UtcNow.AddSeconds(1 << _gatewayModelFailures) : DateTimeOffset.MaxValue;
                    if (_gatewayModelRetryAt != DateTimeOffset.MaxValue)
                    {
                        RequestGatewayRefresh(GatewayRefreshScope.Models);
                    }
                    ShowGatewayError(ex);
                    _gatewayModelErrorText = GatewayErrorText;
                    NotifyGatewayCapabilities();
                }
                return;
            }
        }
        ProjectGatewayModels(_gatewayModelCatalog!, effective, row?.ThinkingLevel);
    }

    private void ProjectGatewayModels(OpenClawModelList models, string effective, string? thinkingLevel)
    {
        var priorSelection = _gatewayModelEdited ? _selectedGatewayModel?.Id : null;
        var priorThinking = _gatewayThinkingEdited ? _selectedGatewayThinkingLevel?.Id : null;
        _gatewayProjectingModel = true;
        try
        {
            if (!ReferenceEquals(_gatewayProjectedModelCatalog, models))
            {
                GatewayModels.Clear();
            }
            if (GatewayModels.Count == 0)
            {
                GatewayModels.Add(new OpenClawModelItem("", GatewayStrings.Text("DefaultModel")));
                foreach (var model in models.Models.Where(model => model.ManualSelectionAllowed == true && model.Available != false))
                {
                    var id = ModelIdentifier(model.Id, model.Provider);
                    if (!string.IsNullOrEmpty(id) && !GatewayModels.Any(item => item.Id == id))
                    {
                        GatewayModels.Add(new OpenClawModelItem(id, string.IsNullOrWhiteSpace(model.Name) ? id : $"{model.Name} · {model.Provider}", model));
                    }
                }
            }
            _gatewayProjectedModelCatalog = models;
            _gatewayServerModelId = effective;
            _gatewayServerThinkingId = thinkingLevel ?? "";
            var currentModel = GatewayModels.FirstOrDefault(model => model.Id == effective);
            var draftModel = priorSelection is null ? null : GatewayModels.FirstOrDefault(model => model.Id == priorSelection);
            GatewayCurrentModelNotice = currentModel is null && effective.Length > 0 ?
                GatewayStrings.Format("CurrentModelUnavailable", effective) : "";
            // A missing or restricted current model is read-only context, never an implicit reset to default.
            SelectedGatewayModel = draftModel ?? currentModel;
            var retainsModelDraft = draftModel is not null && priorSelection != effective;
            _gatewayModelEdited = retainsModelDraft;
            var retainedThinking = priorSelection is null || draftModel is not null ? priorThinking : null;
            var thinking = retainedThinking ?? (retainsModelDraft ? "" : _gatewayServerThinkingId);
            ProjectGatewayThinkingOptions(thinking);
            _gatewayThinkingEdited = SelectedGatewayModel is not null && retainedThinking is not null &&
                retainedThinking == SelectedGatewayThinkingLevel?.Id && retainedThinking != _gatewayServerThinkingId;
        }
        finally
        {
            _gatewayProjectingModel = false;
        }
        OnPropertyChanged(nameof(CanApplyGatewayModel));
    }
}
