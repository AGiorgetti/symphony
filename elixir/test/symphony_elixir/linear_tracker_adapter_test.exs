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
end
