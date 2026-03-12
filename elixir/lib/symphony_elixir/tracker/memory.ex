defmodule SymphonyElixir.Tracker.Memory do
  @moduledoc """
  In-memory tracker adapter used for tests and local development.
  """

  @behaviour SymphonyElixir.Tracker

  alias SymphonyElixir.Config.Schema
  alias SymphonyElixir.Tracker.Issue

  @spec fetch_candidate_issues() :: {:ok, [Issue.t()]} | {:error, term()}
  def fetch_candidate_issues do
    list_active_issues(%Schema{tracker: %Schema.Tracker{active_states: [], terminal_states: []}})
  end

  @spec list_active_issues(Schema.t()) :: {:ok, [Issue.t()]} | {:error, term()}
  def list_active_issues(_settings) do
    {:ok, issue_entries()}
  end

  @spec fetch_issues_by_states([String.t()]) :: {:ok, [Issue.t()]} | {:error, term()}
  def fetch_issues_by_states(state_names) do
    fetch_issues_by_states(state_names, %Schema{tracker: %Schema.Tracker{active_states: [], terminal_states: []}})
  end

  @spec fetch_issues_by_states([String.t()], Schema.t()) :: {:ok, [Issue.t()]} | {:error, term()}
  def fetch_issues_by_states(state_names, _settings) do
    normalized_states =
      state_names
      |> Enum.map(&normalize_state/1)
      |> MapSet.new()

    {:ok,
     Enum.filter(issue_entries(), fn %Issue{state: state} ->
       MapSet.member?(normalized_states, normalize_state(state))
     end)}
  end

  @spec fetch_issue_states_by_ids([String.t()]) :: {:ok, [Issue.t()]} | {:error, term()}
  def fetch_issue_states_by_ids(issue_ids) do
    fetch_issue_states_by_ids(issue_ids, %Schema{tracker: %Schema.Tracker{active_states: [], terminal_states: []}})
  end

  @spec fetch_issue_states_by_ids([String.t()], Schema.t()) :: {:ok, [Issue.t()]} | {:error, term()}
  def fetch_issue_states_by_ids(issue_ids, _settings) do
    wanted_ids = MapSet.new(issue_ids)

    {:ok,
     Enum.filter(issue_entries(), fn %Issue{id: id} ->
       MapSet.member?(wanted_ids, id)
     end)}
  end

  @spec claim_issue(Issue.t(), Schema.t()) :: :ok | {:error, term()}
  def claim_issue(%Issue{id: issue_id}, _settings) do
    send_event({:memory_tracker_claim, issue_id})
    :ok
  end

  @spec create_comment(String.t(), String.t()) :: :ok | {:error, term()}
  def create_comment(issue_id, body) when is_binary(issue_id) and is_binary(body) do
    case post_comment(%Issue{id: issue_id, identifier: issue_id}, body, %Schema{tracker: %Schema.Tracker{}}) do
      {:ok, _comment_id} -> :ok
      {:error, reason} -> {:error, reason}
    end
  end

  @spec post_comment(Issue.t(), String.t(), Schema.t()) :: {:ok, term()} | {:error, term()}
  def post_comment(%Issue{id: issue_id}, body, _settings) do
    send_event({:memory_tracker_comment, issue_id, body})
    {:ok, "memory-comment:#{issue_id}"}
  end

  @spec update_comment(Issue.t(), term(), String.t(), Schema.t()) :: :ok | {:error, term()}
  def update_comment(%Issue{id: issue_id}, comment_id, body, _settings) do
    send_event({:memory_tracker_comment_update, issue_id, comment_id, body})
    :ok
  end

  @spec find_or_create_workpad_comment(Issue.t(), String.t(), Schema.t()) :: {:ok, term()} | {:error, term()}
  def find_or_create_workpad_comment(%Issue{id: issue_id}, marker, _settings) do
    comment_id = "memory-workpad:#{issue_id}"
    send_event({:memory_tracker_workpad, issue_id, marker, comment_id})
    {:ok, comment_id}
  end

  @spec update_issue_state(Issue.t(), String.t(), Schema.t()) :: :ok | {:error, term()}
  def update_issue_state(%Issue{id: issue_id}, state_name, _settings) do
    send_event({:memory_tracker_state_update, issue_id, state_name})
    :ok
  end

  @spec update_issue_state(String.t(), String.t()) :: :ok | {:error, term()}
  def update_issue_state(issue_id, state_name) when is_binary(issue_id) and is_binary(state_name) do
    update_issue_state(%Issue{id: issue_id, identifier: issue_id}, state_name, %Schema{tracker: %Schema.Tracker{}})
  end

  @spec resolve_active_states(Schema.t()) :: [String.t()]
  def resolve_active_states(%Schema{tracker: tracker}), do: tracker.active_states

  @spec resolve_terminal_states(Schema.t()) :: [String.t()]
  def resolve_terminal_states(%Schema{tracker: tracker}), do: tracker.terminal_states

  defp configured_issues do
    Application.get_env(:symphony_elixir, :memory_tracker_issues, [])
  end

  defp issue_entries do
    Enum.filter(configured_issues(), &match?(%Issue{}, &1))
  end

  defp send_event(message) do
    case Application.get_env(:symphony_elixir, :memory_tracker_recipient) do
      pid when is_pid(pid) -> send(pid, message)
      _ -> :ok
    end
  end

  defp normalize_state(state) when is_binary(state) do
    state
    |> String.trim()
    |> String.downcase()
  end

  defp normalize_state(_state), do: ""
end
