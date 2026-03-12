defmodule SymphonyElixir.Tracker do
  @moduledoc """
  Adapter boundary for issue tracker reads and writes.
  """

  alias SymphonyElixir.Config
  alias SymphonyElixir.Config.Schema
  alias SymphonyElixir.Tracker.{Issue, Registry}

  @type comment_id :: term()

  @callback list_active_issues(Schema.t()) :: {:ok, [Issue.t()]} | {:error, term()}
  @callback fetch_issues_by_states([String.t()], Schema.t()) :: {:ok, [Issue.t()]} | {:error, term()}
  @callback fetch_issue_states_by_ids([String.t()], Schema.t()) :: {:ok, [Issue.t()]} | {:error, term()}
  @callback claim_issue(Issue.t(), Schema.t()) :: :ok | {:error, term()}
  @callback post_comment(Issue.t(), String.t(), Schema.t()) :: {:ok, comment_id()} | {:error, term()}
  @callback update_comment(Issue.t(), comment_id(), String.t(), Schema.t()) :: :ok | {:error, term()}
  @callback find_or_create_workpad_comment(Issue.t(), String.t(), Schema.t()) :: {:ok, comment_id()} | {:error, term()}
  @callback update_issue_state(Issue.t(), String.t(), Schema.t()) :: :ok | {:error, term()}
  @callback resolve_active_states(Schema.t()) :: [String.t()]
  @callback resolve_terminal_states(Schema.t()) :: [String.t()]

  @spec fetch_candidate_issues() :: {:ok, [Issue.t()]} | {:error, term()}
  def fetch_candidate_issues do
    with_adapter(fn adapter, settings -> adapter.list_active_issues(settings) end)
  end

  @spec fetch_issues_by_states([String.t()]) :: {:ok, [Issue.t()]} | {:error, term()}
  def fetch_issues_by_states(states) do
    with_adapter(fn adapter, settings -> adapter.fetch_issues_by_states(states, settings) end)
  end

  @spec fetch_issue_states_by_ids([String.t()]) :: {:ok, [Issue.t()]} | {:error, term()}
  def fetch_issue_states_by_ids(issue_ids) do
    with_adapter(fn adapter, settings -> adapter.fetch_issue_states_by_ids(issue_ids, settings) end)
  end

  @spec create_comment(String.t(), String.t()) :: :ok | {:error, term()}
  def create_comment(issue_id, body) do
    issue = %Issue{id: issue_id, identifier: issue_id}

    case post_comment(issue, body) do
      {:ok, _comment_id} -> :ok
      {:error, reason} -> {:error, reason}
    end
  end

  @spec claim_issue(Issue.t()) :: :ok | {:error, term()}
  def claim_issue(%Issue{} = issue) do
    with_adapter(fn adapter, settings -> adapter.claim_issue(issue, settings) end)
  end

  @spec post_comment(Issue.t(), String.t()) :: {:ok, comment_id()} | {:error, term()}
  def post_comment(%Issue{} = issue, body) when is_binary(body) do
    with_adapter(fn adapter, settings -> adapter.post_comment(issue, body, settings) end)
  end

  @spec update_comment(Issue.t(), comment_id(), String.t()) :: :ok | {:error, term()}
  def update_comment(%Issue{} = issue, comment_id, body) when is_binary(body) do
    with_adapter(fn adapter, settings -> adapter.update_comment(issue, comment_id, body, settings) end)
  end

  @spec find_or_create_workpad_comment(Issue.t(), String.t()) :: {:ok, comment_id()} | {:error, term()}
  def find_or_create_workpad_comment(%Issue{} = issue, marker) when is_binary(marker) do
    with_adapter(fn adapter, settings -> adapter.find_or_create_workpad_comment(issue, marker, settings) end)
  end

  @spec update_issue_state(String.t(), String.t()) :: :ok | {:error, term()}
  def update_issue_state(issue_id, state_name)
      when is_binary(issue_id) and is_binary(state_name) do
    issue = %Issue{id: issue_id, identifier: issue_id}
    update_issue_state(issue, state_name)
  end

  @spec update_issue_state(Issue.t(), String.t()) :: :ok | {:error, term()}
  def update_issue_state(%Issue{} = issue, state_name) when is_binary(state_name) do
    with_adapter(fn adapter, settings -> adapter.update_issue_state(issue, state_name, settings) end)
  end

  @spec resolve_active_states() :: [String.t()]
  def resolve_active_states do
    with_adapter(fn adapter, settings -> adapter.resolve_active_states(settings) end, [])
  end

  @spec resolve_terminal_states() :: [String.t()]
  def resolve_terminal_states do
    with_adapter(fn adapter, settings -> adapter.resolve_terminal_states(settings) end, [])
  end

  @spec adapter() :: module()
  def adapter do
    case Registry.resolve(Config.settings!()) do
      {:ok, module} ->
        module

      {:error, reason} ->
        raise ArgumentError, "Unsupported tracker adapter: #{inspect(reason)}"
    end
  end

  defp with_adapter(fun, fallback \\ nil) when is_function(fun, 2) do
    settings = Config.settings!()

    case Registry.resolve(settings) do
      {:ok, module} -> fun.(module, settings)
      {:error, _reason} when not is_nil(fallback) -> fallback
      {:error, reason} -> {:error, reason}
    end
  end
end
