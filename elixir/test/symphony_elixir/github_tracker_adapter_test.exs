defmodule SymphonyElixir.GitHubTrackerAdapterTest do
  use SymphonyElixir.TestSupport

  alias SymphonyElixir.Config.Schema
  alias SymphonyElixir.GitHub.Client
  alias SymphonyElixir.Tracker.GitHub

  defmodule FakeGitHubClient do
    def list_issues(settings, state) do
      send(self(), {:github_list_issues, settings.tracker.repo, state})

      case Process.get({__MODULE__, :issues, state}) do
        {:error, reason} -> {:error, reason}
        issues when is_list(issues) -> {:ok, issues}
        _ -> {:ok, []}
      end
    end

    def get_issue(_settings, issue_id) do
      send(self(), {:github_get_issue, issue_id})

      case Process.get({__MODULE__, :issue, issue_id}) do
        nil -> {:ok, nil}
        {:error, reason} -> {:error, reason}
        issue -> {:ok, issue}
      end
    end

    def list_issue_comments(_settings, issue_id) do
      send(self(), {:github_list_issue_comments, issue_id})

      case Process.get({__MODULE__, :comments_result, issue_id}) do
        {:error, reason} -> {:error, reason}
        nil -> {:ok, Process.get({__MODULE__, :comments, issue_id}, [])}
        comments when is_list(comments) -> {:ok, comments}
      end
    end

    def create_issue_comment(_settings, issue_id, body) do
      send(self(), {:github_create_issue_comment, issue_id, body})

      case Process.get(
             {__MODULE__, :create_comment, issue_id},
             Process.get({__MODULE__, :created_comment})
           ) do
        {:error, reason} -> {:error, reason}
        {:raw, value} -> value
        nil -> {:ok, %{"id" => 101, "body" => body}}
        comment -> {:ok, comment}
      end
    end

    def update_issue_comment(_settings, comment_id, body) do
      send(self(), {:github_update_issue_comment, comment_id, body})

      case Process.get({__MODULE__, :updated_comment_result, comment_id}) do
        {:error, reason} -> {:error, reason}
        nil -> {:ok, %{"id" => comment_id, "body" => body}}
        comment -> {:ok, comment}
      end
    end

    def update_issue(_settings, issue_id, attrs) do
      send(self(), {:github_update_issue, issue_id, attrs})

      case Process.get({__MODULE__, :update_issue_result, issue_id}) do
        {:error, reason} -> {:error, reason}
        nil -> {:ok, attrs}
        issue -> {:ok, issue}
      end
    end
  end

  setup do
    previous_client_module = Application.get_env(:symphony_elixir, :github_client_module)

    Application.put_env(:symphony_elixir, :github_client_module, FakeGitHubClient)

    on_exit(fn ->
      if is_nil(previous_client_module) do
        Application.delete_env(:symphony_elixir, :github_client_module)
      else
        Application.put_env(:symphony_elixir, :github_client_module, previous_client_module)
      end
    end)

    :ok
  end

  test "lists and normalizes active GitHub issues using label-based states" do
    Process.put(
      {FakeGitHubClient, :issues, "open"},
      [
        %{
          "number" => 12,
          "title" => "Implement adapter",
          "body" => "details",
          "state" => "open",
          "html_url" => "https://github.com/octo/demo/issues/12",
          "labels" => [%{"name" => "In Progress"}, %{"name" => "backend"}],
          "assignees" => [%{"login" => "octocat"}],
          "created_at" => "2026-03-12T10:00:00Z",
          "updated_at" => "2026-03-12T11:00:00Z"
        },
        %{
          "number" => 13,
          "title" => "Terminal issue",
          "body" => "done",
          "state" => "closed",
          "html_url" => "https://github.com/octo/demo/issues/13",
          "labels" => [%{"name" => "Done"}],
          "assignees" => [],
          "created_at" => "2026-03-12T10:00:00Z",
          "updated_at" => "2026-03-12T11:00:00Z"
        }
      ]
    )

    settings = github_settings()

    assert {:ok, [%Issue{} = issue]} = GitHub.list_active_issues(settings)
    assert issue.id == "12"
    assert issue.identifier == "octo/demo#12"
    assert issue.state == "In Progress"
    assert issue.assignee_id == "octocat"
    assert issue.labels == ["in progress", "backend"]
    assert_receive {:github_list_issues, "octo/demo", "open"}
  end

  test "ignores pull requests when listing or fetching issues" do
    Process.put(
      {FakeGitHubClient, :issues, "open"},
      [
        %{
          "number" => 70,
          "title" => "Track me",
          "body" => "",
          "state" => "open",
          "labels" => [%{"name" => "Todo"}]
        },
        %{
          "number" => 71,
          "title" => "Actually a PR",
          "body" => "",
          "state" => "open",
          "labels" => [%{"name" => "Todo"}],
          "pull_request" => %{"url" => "https://api.github.com/repos/octo/demo/pulls/71"}
        }
      ]
    )

    Process.put(
      {FakeGitHubClient, :issue, "71"},
      %{
        "number" => 71,
        "title" => "Actually a PR",
        "body" => "",
        "state" => "open",
        "labels" => [%{"name" => "Todo"}],
        "pull_request" => %{"url" => "https://api.github.com/repos/octo/demo/pulls/71"}
      }
    )

    settings = github_settings()

    assert {:ok, issues} = GitHub.list_active_issues(settings)
    assert Enum.map(issues, & &1.id) == ["70"]

    assert {:ok, []} = GitHub.fetch_issue_states_by_ids(["71"], settings)
  end

  test "fetches active and terminal issues by requested states" do
    Process.put(
      {FakeGitHubClient, :issues, "open"},
      [
        %{"number" => 21, "title" => "Todo", "body" => "", "state" => "open", "labels" => [%{"name" => "Todo"}]}
      ]
    )

    Process.put(
      {FakeGitHubClient, :issues, "closed"},
      [
        %{"number" => 22, "title" => "Done", "body" => "", "state" => "closed", "labels" => [%{"name" => "Done"}]}
      ]
    )

    settings = github_settings()

    assert {:ok, issues} = GitHub.fetch_issues_by_states(["Todo", "Done"], settings)
    assert Enum.map(issues, & &1.id) == ["21", "22"]
    assert_receive {:github_list_issues, "octo/demo", "open"}
    assert_receive {:github_list_issues, "octo/demo", "closed"}
  end

  test "updates issue state by replacing workflow labels and opening or closing the issue" do
    Process.put(
      {FakeGitHubClient, :issue, "34"},
      %{
        "number" => 34,
        "title" => "Need review",
        "body" => "",
        "state" => "open",
        "labels" => [%{"name" => "Todo"}, %{"name" => "backend"}]
      }
    )

    settings = github_settings()

    assert :ok = GitHub.update_issue_state(%Issue{id: "34"}, "In Progress", settings)

    assert_receive {:github_update_issue, "34", %{labels: ["backend", "In Progress"], state: "open"}}

    assert :ok = GitHub.update_issue_state(%Issue{id: "34"}, "Done", settings)

    assert_receive {:github_update_issue, "34", %{labels: ["backend", "Done"], state: "closed"}}
  end

  test "finds and updates the persistent workpad comment" do
    Process.put(
      {FakeGitHubClient, :comments, "55"},
      [
        %{"id" => 900, "body" => "## Codex Workpad\n\nhello"},
        %{"id" => 901, "body" => "other"}
      ]
    )

    settings = github_settings()
    issue = %Issue{id: "55", identifier: "octo/demo#55"}

    assert {:ok, 900} = GitHub.find_or_create_workpad_comment(issue, "## Codex Workpad", settings)
    assert :ok = GitHub.update_comment(issue, 900, "updated", settings)

    assert_receive {:github_list_issue_comments, "55"}
    assert_receive {:github_update_issue_comment, 900, "updated"}
  end

  test "creates the workpad comment when it does not exist" do
    Process.put({FakeGitHubClient, :comments, "56"}, [])
    Process.put({FakeGitHubClient, :created_comment}, %{"id" => 777, "body" => "## Codex Workpad"})

    assert {:ok, 777} =
             GitHub.find_or_create_workpad_comment(
               %Issue{id: "56", identifier: "octo/demo#56"},
               "## Codex Workpad",
               github_settings()
             )

    assert_receive {:github_create_issue_comment, "56", "## Codex Workpad"}
  end

  test "github client paginates lists and uses the api token" do
    request_fun = fn opts ->
      page = opts[:params][:page]

      assert Enum.any?(opts[:headers], fn {key, value} ->
               key == "authorization" and value == "Bearer github-token"
             end)

      body =
        if page == 1 do
          for issue_number <- 1..100 do
            %{"number" => issue_number, "state" => "open"}
          end
        else
          [%{"number" => 101, "state" => "open"}]
        end

      {:ok, %{status: 200, body: body}}
    end

    assert {:ok, issues} =
             Client.list_issues(
               github_settings(),
               "open",
               request_fun: request_fun
             )

    assert length(issues) == 101
  end

  test "normalizes github issues with fallback states and assignee matching rules" do
    Process.put(
      {FakeGitHubClient, :issues, "open"},
      [
        %{
          "number" => 91,
          "title" => "Needs defaults",
          "body" => "",
          "state" => "open",
          "labels" => [%{"name" => "backend"}, %{}],
          "assignees" => [%{"login" => 123}],
          "created_at" => "not-a-datetime",
          "updated_at" => 123
        },
        %{
          "number" => 93,
          "title" => "String pull request marker",
          "body" => "",
          "state" => "open",
          "labels" => [%{"name" => "Todo"}],
          "pull_request" => "yes"
        }
      ]
    )

    Process.put(
      {FakeGitHubClient, :issues, "closed"},
      [
        %{
          "number" => 92,
          "title" => "Closed fallback",
          "body" => "",
          "state" => "closed",
          "labels" => [],
          "assignees" => [%{"login" => "someone"}]
        }
      ]
    )

    settings = github_settings(repo: nil, assignee: "octocat")

    assert {:ok, [%Issue{} = open_issue]} = GitHub.list_active_issues(settings)
    assert open_issue.identifier == "repo#91"
    assert open_issue.state == "Todo"
    assert open_issue.assignee_id == nil
    assert open_issue.assigned_to_worker == false
    assert open_issue.labels == ["backend"]
    assert open_issue.created_at == nil
    assert open_issue.updated_at == nil

    assert {:ok, [%Issue{} = closed_issue]} = GitHub.fetch_issues_by_states(["Done", nil], settings)
    assert closed_issue.id == "92"
    assert closed_issue.state == "Done"

    assert {:ok, [%Issue{} = assigned_issue]} =
             GitHub.list_active_issues(github_settings(assignee: 123, active_states: ["Todo"]))

    assert assigned_issue.assigned_to_worker == true
  end

  test "skips unused state buckets and deduplicates state lookups" do
    Process.put(
      {FakeGitHubClient, :issues, "open"},
      [
        %{"number" => 101, "title" => "Todo", "body" => "", "state" => "open", "labels" => [%{"name" => "Todo"}]}
      ]
    )

    Process.put(
      {FakeGitHubClient, :issues, "closed"},
      [
        %{"number" => 102, "title" => "Done", "body" => "", "state" => "closed", "labels" => [%{"name" => "Done"}]}
      ]
    )

    Process.put(
      {FakeGitHubClient, :issue, "200"},
      %{"number" => 200, "title" => "Todo", "body" => "", "state" => "open", "labels" => [%{"name" => "Todo"}]}
    )

    settings = github_settings()

    assert {:ok, [%Issue{id: "101"}]} = GitHub.fetch_issues_by_states(["Todo"], settings)
    assert_receive {:github_list_issues, "octo/demo", "open"}
    refute_receive {:github_list_issues, "octo/demo", "closed"}

    assert {:ok, [%Issue{id: "102"}]} = GitHub.fetch_issues_by_states(["Done"], settings)
    assert_receive {:github_list_issues, "octo/demo", "closed"}

    assert {:ok, [%Issue{id: "200"}]} = GitHub.fetch_issue_states_by_ids(["200", "200", "404"], settings)
    assert_receive {:github_get_issue, "200"}
    assert_receive {:github_get_issue, "404"}
    refute_receive {:github_get_issue, "200"}
  end

  test "propagates github tracker read and write failures" do
    settings = github_settings()
    issue = %Issue{id: "301", identifier: "octo/demo#301"}

    Process.put({FakeGitHubClient, :issues, "open"}, {:error, :unavailable})
    assert {:error, :unavailable} = GitHub.list_active_issues(settings)

    Process.put({FakeGitHubClient, :issue, "302"}, {:error, :lookup_failed})
    assert {:error, :lookup_failed} = GitHub.fetch_issue_states_by_ids(["302"], settings)

    Process.put(
      {FakeGitHubClient, :issue, "303"},
      %{"number" => 303, "title" => "No labels", "body" => "", "state" => "open"}
    )

    assert {:ok, [%Issue{id: "303", labels: []}]} = GitHub.fetch_issue_states_by_ids(["303"], settings)

    assert :ok = GitHub.claim_issue(issue, settings)
    assert GitHub.resolve_active_states(settings) == ["Todo", "In Progress"]
    assert GitHub.resolve_terminal_states(settings) == ["Done", "Closed"]

    assert {:error, :invalid_issue_id} = GitHub.post_comment(%Issue{id: nil}, "hello", settings)
    Process.put({FakeGitHubClient, :create_comment, "301"}, %{"body" => "missing id"})
    assert {:error, :comment_create_failed} = GitHub.post_comment(issue, "hello", settings)

    Process.put({FakeGitHubClient, :create_comment, "301"}, {:error, :comment_denied})
    assert {:error, :comment_denied} = GitHub.post_comment(issue, "hello", settings)

    Process.put({FakeGitHubClient, :create_comment, "301"}, {:raw, :unexpected})
    assert {:error, :comment_create_failed} = GitHub.post_comment(issue, "hello", settings)

    assert {:error, :invalid_comment_id} = GitHub.update_comment(issue, nil, "updated", settings)
    Process.put({FakeGitHubClient, :updated_comment_result, 77}, {:error, :update_denied})
    assert {:error, :update_denied} = GitHub.update_comment(issue, 77, "updated", settings)

    Process.put({FakeGitHubClient, :comments_result, "301"}, {:error, :comment_lookup_failed})

    assert {:error, :comment_lookup_failed} =
             GitHub.find_or_create_workpad_comment(issue, "## Codex Workpad", settings)

    assert {:error, :invalid_issue_id} =
             GitHub.find_or_create_workpad_comment(%Issue{id: nil}, "## Codex Workpad", settings)

    Process.put(
      {FakeGitHubClient, :comments_result, "301"},
      [%{"body" => nil}, %{"id" => nil, "body" => "## Codex Workpad"}]
    )

    Process.put({FakeGitHubClient, :create_comment, "301"}, %{"id" => 333, "body" => "## Codex Workpad"})

    assert {:ok, 333} =
             GitHub.find_or_create_workpad_comment(issue, "## Codex Workpad", settings)
  end

  test "validates github issue state updates" do
    settings = github_settings()

    assert {:error, :invalid_issue_id} = GitHub.update_issue_state(%Issue{id: nil}, "Done", settings)

    Process.put({FakeGitHubClient, :issue, "401"}, nil)
    assert {:error, :issue_not_found} = GitHub.update_issue_state(%Issue{id: "401"}, "Done", settings)

    Process.put(
      {FakeGitHubClient, :issue, "402"},
      %{"number" => 402, "title" => "Todo", "body" => "", "state" => "open", "labels" => [%{"name" => "Todo"}]}
    )

    assert {:error, {:unknown_tracker_state, "Blocked"}} =
             GitHub.update_issue_state(%Issue{id: "402"}, "Blocked", settings)

    Process.put({FakeGitHubClient, :update_issue_result, "402"}, {:error, :update_failed})
    assert {:error, :update_failed} = GitHub.update_issue_state(%Issue{id: "402"}, "Done", settings)
  end

  defp github_settings(overrides \\ []) do
    tracker =
      struct!(
        Schema.Tracker,
        Keyword.merge(
          [
            kind: "github",
            api_token: "github-token",
            repo: "octo/demo",
            assignee: nil,
            active_states: ["Todo", "In Progress"],
            terminal_states: ["Done", "Closed"]
          ],
          overrides
        )
      )

    %Schema{tracker: tracker}
  end
end
