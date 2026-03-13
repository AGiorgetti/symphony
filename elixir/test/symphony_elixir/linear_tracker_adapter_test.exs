defmodule SymphonyElixir.LinearTrackerAdapterTest do
  use SymphonyElixir.TestSupport

  alias SymphonyElixir.Tracker.Linear

  defmodule FakeLinearTrackerClient do
    def fetch_candidate_issues do
      send(self(), :linear_fetch_candidate_issues)
      {:ok, [%Issue{id: "lin-1", identifier: "MT-1", state: "Todo"}]}
    end

    def fetch_issues_by_states(states) do
      send(self(), {:linear_fetch_issues_by_states, states})
      {:ok, Enum.map(states, &%Issue{id: &1, identifier: &1, state: &1})}
    end

    def fetch_issue_states_by_ids(issue_ids) do
      send(self(), {:linear_fetch_issue_states_by_ids, issue_ids})
      {:ok, Enum.map(issue_ids, &%Issue{id: &1, identifier: &1, state: "In Progress"})}
    end

    def graphql(query, variables) do
      send(self(), {:linear_graphql, query, variables})

      case Process.get({__MODULE__, :graphql_results}) do
        [result | rest] ->
          Process.put({__MODULE__, :graphql_results}, rest)
          result

        nil ->
          Process.get({__MODULE__, :graphql_result})
      end
    end
  end

  setup do
    previous_client_module = Application.get_env(:symphony_elixir, :linear_client_module)

    Application.put_env(:symphony_elixir, :linear_client_module, FakeLinearTrackerClient)

    on_exit(fn ->
      if is_nil(previous_client_module) do
        Application.delete_env(:symphony_elixir, :linear_client_module)
      else
        Application.put_env(:symphony_elixir, :linear_client_module, previous_client_module)
      end
    end)

    :ok
  end

  test "delegates read operations to the Linear client" do
    assert {:ok, [%Issue{id: "lin-1"}]} = Linear.list_active_issues(Config.settings!())
    assert_receive :linear_fetch_candidate_issues

    assert {:ok, [%Issue{id: "Todo"}]} = Linear.fetch_issues_by_states(["Todo"], Config.settings!())
    assert_receive {:linear_fetch_issues_by_states, ["Todo"]}

    assert {:ok, [%Issue{id: "issue-1"}]} =
             Linear.fetch_issue_states_by_ids(["issue-1"], Config.settings!())

    assert_receive {:linear_fetch_issue_states_by_ids, ["issue-1"]}
  end

  test "creates and updates comments when the Linear API supports it" do
    Process.put(
      {FakeLinearTrackerClient, :graphql_results},
      [
        {:ok, %{"data" => %{"commentCreate" => %{"success" => true, "comment" => %{"id" => "comment-1"}}}}},
        {:ok, %{"data" => %{"commentUpdate" => %{"success" => true, "comment" => %{"id" => "comment-1"}}}}}
      ]
    )

    issue = %Issue{id: "linear-1", identifier: "MT-1"}

    assert {:ok, "comment-1"} = Linear.post_comment(issue, "hello", Config.settings!())
    assert_receive {:linear_graphql, create_query, %{body: "hello", issueId: "linear-1"}}
    assert create_query =~ "commentCreate"

    assert :ok = Linear.update_comment(issue, "comment-1", "updated", Config.settings!())
    assert_receive {:linear_graphql, update_query, %{body: "updated", commentId: "comment-1"}}
    assert update_query =~ "commentUpdate"
  end

  test "returns explicit unsupported when Linear comment update is unavailable" do
    Process.put(
      {FakeLinearTrackerClient, :graphql_result},
      {:ok, %{"errors" => [%{"message" => "commentUpdate unsupported"}]}}
    )

    assert {:error, :update_comment_unsupported} =
             Linear.update_comment(%Issue{id: "linear-1"}, "comment-1", "updated", Config.settings!())
  end

  test "finds an existing workpad comment before creating a new one" do
    Process.put(
      {FakeLinearTrackerClient, :graphql_result},
      {:ok,
       %{
         "data" => %{
           "issue" => %{
             "comments" => %{
               "nodes" => [
                 %{"id" => "comment-2", "body" => "## Codex Workpad\n\nstatus"},
                 %{"id" => "comment-3", "body" => "other"}
               ]
             }
           }
         }
       }}
    )

    assert {:ok, "comment-2"} =
             Linear.find_or_create_workpad_comment(
               %Issue{id: "linear-2"},
               "## Codex Workpad",
               Config.settings!()
             )
  end

  test "creates the workpad comment when no existing marker is present" do
    Process.put(
      {FakeLinearTrackerClient, :graphql_results},
      [
        {:ok, %{"data" => %{"issue" => %{"comments" => %{"nodes" => []}}}}},
        {:ok, %{"data" => %{"commentCreate" => %{"success" => true, "comment" => %{"id" => "comment-9"}}}}}
      ]
    )

    assert {:ok, "comment-9"} =
             Linear.find_or_create_workpad_comment(
               %Issue{id: "linear-3"},
               "## Codex Workpad",
               Config.settings!()
             )
  end

  test "covers invalid identifiers and comment mutation failure branches" do
    settings = Config.settings!()
    issue = %Issue{id: "linear-4", identifier: "MT-4"}

    assert :ok = Linear.claim_issue(issue, settings)
    assert {:error, :invalid_issue_id} = Linear.post_comment(%Issue{id: nil}, "hello", settings)

    Process.put(
      {FakeLinearTrackerClient, :graphql_result},
      {:ok, %{"data" => %{"commentCreate" => %{"success" => false}}}}
    )

    assert {:error, :comment_create_failed} = Linear.post_comment(issue, "hello", settings)

    Process.put(
      {FakeLinearTrackerClient, :graphql_result},
      {:ok, %{"data" => %{"commentCreate" => %{"success" => true}}}}
    )

    assert {:error, :comment_create_failed} = Linear.post_comment(issue, "hello", settings)

    Process.put({FakeLinearTrackerClient, :graphql_result}, {:error, :boom})
    assert {:error, :boom} = Linear.post_comment(issue, "hello", settings)

    Process.put(
      {FakeLinearTrackerClient, :graphql_result},
      {:ok, %{"errors" => [%{"message" => "commentCreate unsupported"}]}}
    )

    assert {:error, :comment_create_unsupported} = Linear.post_comment(issue, "hello", settings)

    assert {:error, :invalid_comment_id} =
             Linear.update_comment(issue, nil, "updated", settings)

    Process.put({FakeLinearTrackerClient, :graphql_result}, {:error, :update_failed})
    assert {:error, :update_failed} = Linear.update_comment(issue, "comment-1", "updated", settings)

    Process.put(
      {FakeLinearTrackerClient, :graphql_result},
      {:ok, %{"data" => %{"commentUpdate" => %{"success" => false}}}}
    )

    assert {:error, :comment_update_failed} =
             Linear.update_comment(issue, "comment-1", "updated", settings)

    Process.put({FakeLinearTrackerClient, :graphql_result}, {:ok, %{"data" => %{}}})

    assert {:error, :comment_update_failed} =
             Linear.update_comment(issue, "comment-1", "updated", settings)
  end

  test "covers workpad lookup failure and unsupported branches" do
    issue = %Issue{id: "linear-5", identifier: "MT-5"}
    settings = Config.settings!()

    Process.put(
      {FakeLinearTrackerClient, :graphql_result},
      {:ok, %{"errors" => [%{"message" => "comments unsupported"}]}}
    )

    assert {:error, :comment_lookup_unsupported} =
             Linear.find_or_create_workpad_comment(issue, "## Codex Workpad", settings)

    Process.put({FakeLinearTrackerClient, :graphql_result}, {:ok, %{"data" => %{"issue" => %{}}}})

    assert {:error, :comment_lookup_failed} =
             Linear.find_or_create_workpad_comment(issue, "## Codex Workpad", settings)

    Process.put(
      {FakeLinearTrackerClient, :graphql_results},
      [
        {:ok,
         %{
           "data" => %{
             "issue" => %{"comments" => %{"nodes" => [%{"id" => "ignored", "body" => nil}]}}
           }
         }},
        {:ok, %{"data" => %{"commentCreate" => %{"success" => true, "comment" => %{"id" => "comment-10"}}}}}
      ]
    )

    assert {:ok, "comment-10"} =
             Linear.find_or_create_workpad_comment(issue, "## Codex Workpad", settings)

    assert {:error, :invalid_issue_id} =
             Linear.find_or_create_workpad_comment(%Issue{id: nil}, "## Codex Workpad", settings)
  end

  test "updates issue state and surfaces lookup failures" do
    settings = Config.settings!()
    issue = %Issue{id: "linear-6", identifier: "MT-6"}

    Process.put(
      {FakeLinearTrackerClient, :graphql_results},
      [
        {:ok,
         %{
           "data" => %{
             "issue" => %{"team" => %{"states" => %{"nodes" => [%{"id" => "state-1"}]}}}
           }
         }},
        {:ok, %{"data" => %{"issueUpdate" => %{"success" => true}}}}
      ]
    )

    assert :ok = Linear.update_issue_state(issue, "Done", settings)

    Process.put({FakeLinearTrackerClient, :graphql_results}, [{:error, :lookup_failed}])
    assert {:error, :lookup_failed} = Linear.update_issue_state(issue, "Done", settings)

    Process.put(
      {FakeLinearTrackerClient, :graphql_results},
      [{:ok, %{"errors" => [%{"message" => "state lookup unsupported"}]}}]
    )

    assert {:error, :state_lookup_unsupported} =
             Linear.update_issue_state(issue, "Done", settings)

    Process.put({FakeLinearTrackerClient, :graphql_results}, [{:ok, %{"data" => %{}}}])
    assert {:error, :state_not_found} = Linear.update_issue_state(issue, "Done", settings)

    Process.put(
      {FakeLinearTrackerClient, :graphql_results},
      [
        {:ok,
         %{
           "data" => %{
             "issue" => %{"team" => %{"states" => %{"nodes" => [%{"id" => "state-2"}]}}}
           }
         }},
        {:ok, %{"data" => %{"issueUpdate" => %{"success" => false}}}}
      ]
    )

    assert {:error, :issue_update_failed} =
             Linear.update_issue_state(issue, "Done", settings)

    Process.put(
      {FakeLinearTrackerClient, :graphql_results},
      [
        {:ok,
         %{
           "data" => %{
             "issue" => %{"team" => %{"states" => %{"nodes" => [%{"id" => "state-3"}]}}}
           }
         }},
        {:ok, %{"data" => %{}}}
      ]
    )

    assert {:error, :issue_update_failed} =
             Linear.update_issue_state(issue, "Done", settings)

    assert {:error, :invalid_issue_id} = Linear.update_issue_state(%Issue{id: nil}, "Done", settings)
    assert Linear.resolve_active_states(settings) == ["Todo", "In Progress"]
    assert Linear.resolve_terminal_states(settings) == ["Closed", "Cancelled", "Canceled", "Duplicate", "Done"]
  end
end
