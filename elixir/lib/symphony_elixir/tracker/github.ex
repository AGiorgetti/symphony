defmodule SymphonyElixir.Tracker.GitHub do
  @moduledoc """
  GitHub Issues-backed tracker adapter.
  """

  @behaviour SymphonyElixir.Tracker

  alias SymphonyElixir.Config.Schema
  alias SymphonyElixir.GitHub.Client
  alias SymphonyElixir.Tracker.Issue

  @spec list_active_issues(Schema.t()) :: {:ok, [Issue.t()]} | {:error, term()}
  def list_active_issues(%Schema{} = settings) do
    with {:ok, issues} <- client_module().list_issues(settings, "open") do
      issues
      |> Enum.reject(&pull_request_issue?/1)
      |> Enum.map(&normalize_issue(&1, settings))
      |> Enum.filter(&state_in?(&1.state, settings.tracker.active_states))
      |> then(&{:ok, &1})
    end
  end

  @spec fetch_issues_by_states([String.t()], Schema.t()) :: {:ok, [Issue.t()]} | {:error, term()}
  def fetch_issues_by_states(state_names, %Schema{} = settings) when is_list(state_names) do
    requested_states = normalize_names(state_names)
    active_states = normalize_names(settings.tracker.active_states)
    terminal_states = normalize_names(settings.tracker.terminal_states)

    with {:ok, open_issues} <-
           maybe_list_issues(settings, "open", not MapSet.disjoint?(requested_states, active_states)),
         {:ok, closed_issues} <-
           maybe_list_issues(settings, "closed", not MapSet.disjoint?(requested_states, terminal_states)) do
      (open_issues ++ closed_issues)
      |> Enum.reject(&pull_request_issue?/1)
      |> Enum.map(&normalize_issue(&1, settings))
      |> Enum.filter(&MapSet.member?(requested_states, normalize_name(&1.state)))
      |> then(&{:ok, &1})
    end
  end

  @spec fetch_issue_states_by_ids([String.t()], Schema.t()) :: {:ok, [Issue.t()]} | {:error, term()}
  def fetch_issue_states_by_ids(issue_ids, %Schema{} = settings) when is_list(issue_ids) do
    issue_ids
    |> Enum.uniq()
    |> Enum.reduce_while({:ok, []}, fn issue_id, {:ok, acc} ->
      case client_module().get_issue(settings, issue_id) do
        {:ok, nil} ->
          {:cont, {:ok, acc}}

        {:ok, issue} when pull_request_issue?(issue) ->
          {:cont, {:ok, acc}}

        {:ok, issue} ->
          {:cont, {:ok, acc ++ [normalize_issue(issue, settings)]}}

        {:error, reason} ->
          {:halt, {:error, reason}}
      end
    end)
  end

  @spec claim_issue(Issue.t(), Schema.t()) :: :ok | {:error, term()}
  def claim_issue(_issue, _settings), do: :ok

  @spec post_comment(Issue.t(), String.t(), Schema.t()) :: {:ok, term()} | {:error, term()}
  def post_comment(%Issue{id: issue_id}, body, %Schema{} = settings)
      when is_binary(issue_id) and is_binary(body) do
    with {:ok, comment} <- client_module().create_issue_comment(settings, issue_id, body),
         comment_id when not is_nil(comment_id) <- comment["id"] do
      {:ok, comment_id}
    else
      nil -> {:error, :comment_create_failed}
      {:error, reason} -> {:error, reason}
      _ -> {:error, :comment_create_failed}
    end
  end

  def post_comment(_issue, _body, _settings), do: {:error, :invalid_issue_id}

  @spec update_comment(Issue.t(), term(), String.t(), Schema.t()) :: :ok | {:error, term()}
  def update_comment(_issue, comment_id, body, %Schema{} = settings)
      when (is_integer(comment_id) or is_binary(comment_id)) and is_binary(body) do
    with {:ok, _comment} <- client_module().update_issue_comment(settings, comment_id, body) do
      :ok
    end
  end

  def update_comment(_issue, _comment_id, _body, _settings), do: {:error, :invalid_comment_id}

  @spec find_or_create_workpad_comment(Issue.t(), String.t(), Schema.t()) :: {:ok, term()} | {:error, term()}
  def find_or_create_workpad_comment(%Issue{id: issue_id} = issue, marker, %Schema{} = settings)
      when is_binary(issue_id) and is_binary(marker) do
    with {:ok, comments} <- client_module().list_issue_comments(settings, issue_id) do
      case Enum.find(comments, &comment_matches_marker?(&1, marker)) do
        %{"id" => comment_id} when not is_nil(comment_id) ->
          {:ok, comment_id}

        _ ->
          post_comment(issue, marker, settings)
      end
    end
  end

  def find_or_create_workpad_comment(_issue, _marker, _settings), do: {:error, :invalid_issue_id}

  @spec update_issue_state(Issue.t(), String.t(), Schema.t()) :: :ok | {:error, term()}
  def update_issue_state(%Issue{id: issue_id}, state_name, %Schema{} = settings)
      when is_binary(issue_id) and is_binary(state_name) do
    with {:ok, current_issue} <- client_module().get_issue(settings, issue_id),
         {:ok, attrs} <- issue_update_attrs(current_issue, state_name, settings),
         {:ok, _issue} <- client_module().update_issue(settings, issue_id, attrs) do
      :ok
    end
  end

  def update_issue_state(_issue, _state_name, _settings), do: {:error, :invalid_issue_id}

  @spec resolve_active_states(Schema.t()) :: [String.t()]
  def resolve_active_states(%Schema{tracker: tracker}), do: tracker.active_states

  @spec resolve_terminal_states(Schema.t()) :: [String.t()]
  def resolve_terminal_states(%Schema{tracker: tracker}), do: tracker.terminal_states

  defp client_module do
    Application.get_env(:symphony_elixir, :github_client_module, Client)
  end

  defp maybe_list_issues(_settings, _state, false), do: {:ok, []}
  defp maybe_list_issues(settings, state, true), do: client_module().list_issues(settings, state)

  defp pull_request_issue?(%{"pull_request" => %{} = _pull_request}), do: true
  defp pull_request_issue?(%{"pull_request" => _pull_request}), do: true
  defp pull_request_issue?(_issue), do: false

  defp normalize_issue(%{} = issue, %Schema{} = settings) do
    number = issue["number"] |> to_string()
    repo = settings.tracker.repo || "repo"
    labels = label_names(issue)

    %Issue{
      id: number,
      identifier: "#{repo}##{number}",
      title: issue["title"],
      description: issue["body"],
      state: resolve_issue_state(issue, labels, settings),
      url: issue["html_url"],
      assignee_id: extract_assignee(issue),
      labels: Enum.map(labels, &String.downcase/1),
      assigned_to_worker: assigned_to_worker?(issue, settings),
      created_at: parse_datetime(issue["created_at"]),
      updated_at: parse_datetime(issue["updated_at"]),
      meta: %{
        "number" => issue["number"],
        "repo" => repo,
        "labels" => labels
      }
    }
  end

  defp resolve_issue_state(issue, labels, %Schema{} = settings) do
    cond do
      issue["state"] == "closed" ->
        pick_state_label(labels, settings.tracker.terminal_states) ||
          List.first(settings.tracker.terminal_states) ||
          "Closed"

      true ->
        pick_state_label(labels, settings.tracker.active_states) ||
          List.first(settings.tracker.active_states) ||
          "Open"
    end
  end

  defp issue_update_attrs(nil, _state_name, _settings), do: {:error, :issue_not_found}

  defp issue_update_attrs(%{} = issue, state_name, %Schema{} = settings) do
    normalized_state = normalize_name(state_name)
    active_states = normalize_names(settings.tracker.active_states)
    terminal_states = normalize_names(settings.tracker.terminal_states)
    current_labels = label_names(issue)
    cleaned_labels = remove_workflow_labels(current_labels, settings)

    cond do
      MapSet.member?(active_states, normalized_state) ->
        {:ok, %{state: "open", labels: cleaned_labels ++ [state_name]}}

      MapSet.member?(terminal_states, normalized_state) ->
        {:ok, %{state: "closed", labels: cleaned_labels ++ [state_name]}}

      true ->
        {:error, {:unknown_tracker_state, state_name}}
    end
  end

  defp label_names(%{"labels" => labels}) when is_list(labels) do
    labels
    |> Enum.map(fn
      %{"name" => name} when is_binary(name) -> name
      _ -> nil
    end)
    |> Enum.reject(&is_nil/1)
  end

  defp label_names(_issue), do: []

  defp extract_assignee(%{"assignees" => assignees}) when is_list(assignees) do
    assignees
    |> Enum.find_value(fn
      %{"login" => login} when is_binary(login) -> login
      _ -> nil
    end)
  end

  defp extract_assignee(_issue), do: nil

  defp assigned_to_worker?(_issue, %Schema{tracker: %{assignee: nil}}), do: true

  defp assigned_to_worker?(issue, %Schema{tracker: %{assignee: assignee}})
       when is_binary(assignee) do
    normalize_name(extract_assignee(issue)) == normalize_name(assignee)
  end

  defp assigned_to_worker?(_issue, _settings), do: true

  defp remove_workflow_labels(labels, %Schema{} = settings) when is_list(labels) do
    workflow_labels =
      settings.tracker.active_states
      |> Kernel.++(settings.tracker.terminal_states)
      |> normalize_names()

    Enum.reject(labels, fn label ->
      MapSet.member?(workflow_labels, normalize_name(label))
    end)
  end

  defp pick_state_label(labels, configured_states) when is_list(labels) and is_list(configured_states) do
    configured_lookup =
      configured_states
      |> Enum.map(fn state -> {normalize_name(state), state} end)
      |> Map.new()

    labels
    |> Enum.find_value(fn label ->
      Map.get(configured_lookup, normalize_name(label))
    end)
  end

  defp state_in?(state, configured_states) do
    MapSet.member?(normalize_names(configured_states), normalize_name(state))
  end

  defp normalize_names(names) when is_list(names) do
    names
    |> Enum.map(&normalize_name/1)
    |> MapSet.new()
  end

  defp normalize_name(name) when is_binary(name) do
    name
    |> String.trim()
    |> String.downcase()
  end

  defp normalize_name(_name), do: ""

  defp parse_datetime(nil), do: nil

  defp parse_datetime(value) when is_binary(value) do
    case DateTime.from_iso8601(value) do
      {:ok, datetime, _offset} -> datetime
      _ -> nil
    end
  end

  defp parse_datetime(_value), do: nil

  defp comment_matches_marker?(%{"body" => body}, marker)
       when is_binary(body) and is_binary(marker) do
    String.contains?(body, marker)
  end

  defp comment_matches_marker?(_comment, _marker), do: false
end
