defmodule SymphonyElixir.Tracker.Linear do
  @moduledoc """
  Linear-backed tracker adapter.
  """

  @behaviour SymphonyElixir.Tracker

  alias SymphonyElixir.Config.Schema
  alias SymphonyElixir.Linear.Client
  alias SymphonyElixir.Tracker.Issue

  @create_comment_mutation """
  mutation SymphonyCreateComment($issueId: String!, $body: String!) {
    commentCreate(input: {issueId: $issueId, body: $body}) {
      success
      comment {
        id
      }
    }
  }
  """

  @update_comment_mutation """
  mutation SymphonyUpdateComment($commentId: String!, $body: String!) {
    commentUpdate(id: $commentId, input: {body: $body}) {
      success
      comment {
        id
      }
    }
  }
  """

  @issue_comments_query """
  query SymphonyIssueComments($issueId: String!, $first: Int!) {
    issue(id: $issueId) {
      comments(first: $first) {
        nodes {
          id
          body
        }
      }
    }
  }
  """

  @update_state_mutation """
  mutation SymphonyUpdateIssueState($issueId: String!, $stateId: String!) {
    issueUpdate(id: $issueId, input: {stateId: $stateId}) {
      success
    }
  }
  """

  @state_lookup_query """
  query SymphonyResolveStateId($issueId: String!, $stateName: String!) {
    issue(id: $issueId) {
      team {
        states(filter: {name: {eq: $stateName}}, first: 1) {
          nodes {
            id
          }
        }
      }
    }
  }
  """

  @comment_page_size 100

  @spec list_active_issues(Schema.t()) :: {:ok, [Issue.t()]} | {:error, term()}
  def list_active_issues(_settings), do: client_module().fetch_candidate_issues()

  @spec fetch_issues_by_states([String.t()], Schema.t()) :: {:ok, [Issue.t()]} | {:error, term()}
  def fetch_issues_by_states(states, _settings), do: client_module().fetch_issues_by_states(states)

  @spec fetch_issue_states_by_ids([String.t()], Schema.t()) :: {:ok, [Issue.t()]} | {:error, term()}
  def fetch_issue_states_by_ids(issue_ids, _settings), do: client_module().fetch_issue_states_by_ids(issue_ids)

  @spec claim_issue(Issue.t(), Schema.t()) :: :ok | {:error, term()}
  def claim_issue(_issue, _settings), do: :ok

  @spec post_comment(Issue.t(), String.t(), Schema.t()) :: {:ok, term()} | {:error, term()}
  def post_comment(%Issue{id: issue_id}, body, _settings)
      when is_binary(issue_id) and is_binary(body) do
    with {:ok, response} <- graphql(@create_comment_mutation, %{issueId: issue_id, body: body}),
         {:ok, comment_id} <- extract_comment_id(response, ["data", "commentCreate"]) do
      {:ok, comment_id}
    else
      {:error, reason} ->
        {:error, reason}

      :unsupported ->
        {:error, :comment_create_unsupported}
    end
  end

  def post_comment(_issue, _body, _settings), do: {:error, :invalid_issue_id}

  @spec update_comment(Issue.t(), term(), String.t(), Schema.t()) :: :ok | {:error, term()}
  def update_comment(_issue, comment_id, body, _settings)
      when is_binary(body) and (is_binary(comment_id) or is_integer(comment_id)) do
    with {:ok, response} <-
           graphql(@update_comment_mutation, %{commentId: to_string(comment_id), body: body}),
         true <- mutation_success?(response, ["data", "commentUpdate"]) do
      :ok
    else
      {:error, reason} ->
        {:error, reason}

      false ->
        {:error, :comment_update_failed}

      :unsupported ->
        {:error, :update_comment_unsupported}
    end
  end

  def update_comment(_issue, _comment_id, _body, _settings), do: {:error, :invalid_comment_id}

  @spec find_or_create_workpad_comment(Issue.t(), String.t(), Schema.t()) :: {:ok, term()} | {:error, term()}
  def find_or_create_workpad_comment(%Issue{id: issue_id} = issue, marker, settings)
      when is_binary(issue_id) and is_binary(marker) do
    with {:ok, response} <-
           graphql(@issue_comments_query, %{issueId: issue_id, first: @comment_page_size}),
         {:ok, comments} <- extract_issue_comments(response) do
      case Enum.find(comments, &comment_matches_marker?(&1, marker)) do
        %{"id" => comment_id} when is_binary(comment_id) ->
          {:ok, comment_id}

        _ ->
          post_comment(issue, marker, settings)
      end
    else
      :unsupported ->
        {:error, :comment_lookup_unsupported}

      {:error, reason} ->
        {:error, reason}
    end
  end

  def find_or_create_workpad_comment(_issue, _marker, _settings), do: {:error, :invalid_issue_id}

  @spec update_issue_state(Issue.t(), String.t(), Schema.t()) :: :ok | {:error, term()}
  def update_issue_state(%Issue{id: issue_id}, state_name, _settings)
      when is_binary(issue_id) and is_binary(state_name) do
    with {:ok, state_id} <- resolve_state_id(issue_id, state_name),
         {:ok, response} <- graphql(@update_state_mutation, %{issueId: issue_id, stateId: state_id}),
         true <- mutation_success?(response, ["data", "issueUpdate"]) do
      :ok
    else
      {:error, reason} -> {:error, reason}
      false -> {:error, :issue_update_failed}
    end
  end

  def update_issue_state(_issue, _state_name, _settings), do: {:error, :invalid_issue_id}

  @spec resolve_active_states(Schema.t()) :: [String.t()]
  def resolve_active_states(%Schema{tracker: tracker}), do: tracker.active_states

  @spec resolve_terminal_states(Schema.t()) :: [String.t()]
  def resolve_terminal_states(%Schema{tracker: tracker}), do: tracker.terminal_states

  defp client_module do
    Application.get_env(:symphony_elixir, :linear_client_module, Client)
  end

  defp graphql(query, variables) do
    case client_module().graphql(query, variables) do
      {:ok, %{"errors" => _errors}} ->
        :unsupported

      {:ok, response} ->
        {:ok, response}

      {:error, reason} ->
        {:error, reason}
    end
  end

  defp resolve_state_id(issue_id, state_name) do
    with {:ok, response} <- graphql(@state_lookup_query, %{issueId: issue_id, stateName: state_name}),
         state_id when is_binary(state_id) <-
           get_in(response, ["data", "issue", "team", "states", "nodes", Access.at(0), "id"]) do
      {:ok, state_id}
    else
      {:error, reason} -> {:error, reason}
      :unsupported -> {:error, :state_lookup_unsupported}
      _ -> {:error, :state_not_found}
    end
  end

  defp mutation_success?(response, path) when is_map(response) and is_list(path) do
    get_in(response, path ++ ["success"]) == true
  end

  defp extract_comment_id(response, path) when is_map(response) and is_list(path) do
    cond do
      get_in(response, path ++ ["success"]) != true ->
        {:error, :comment_create_failed}

      comment_id = get_in(response, path ++ ["comment", "id"]) ->
        {:ok, comment_id}

      true ->
        {:error, :comment_create_failed}
    end
  end

  defp extract_issue_comments(%{"data" => %{"issue" => %{"comments" => %{"nodes" => comments}}}})
       when is_list(comments) do
    {:ok, comments}
  end

  defp extract_issue_comments(_response), do: {:error, :comment_lookup_failed}

  defp comment_matches_marker?(%{"body" => body}, marker)
       when is_binary(body) and is_binary(marker) do
    String.contains?(body, marker)
  end

  defp comment_matches_marker?(_comment, _marker), do: false
end
