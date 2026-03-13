defmodule SymphonyElixir.GitHubClientTest do
  use SymphonyElixir.TestSupport

  alias SymphonyElixir.Config.Schema
  alias SymphonyElixir.GitHub.Client

  test "list_issues paginates and uses env token with a custom endpoint" do
    previous_token = System.get_env("GITHUB_TOKEN")
    on_exit(fn -> restore_env("GITHUB_TOKEN", previous_token) end)
    System.put_env("GITHUB_TOKEN", "env-token")

    request_fun = fn opts ->
      send(self(), {:request, opts})

      if opts[:params][:page] == 1 do
        {:ok,
         %{
           status: 200,
           body: for(issue_number <- 1..100, do: %{"number" => issue_number, "state" => "open"})
         }}
      else
        {:ok, %Req.Response{status: 200, body: [%{"number" => 101, "state" => "open"}]}}
      end
    end

    assert {:ok, issues} =
             Client.list_issues(
               github_settings(api_token: nil, endpoint: "https://github.example.test/"),
               "open",
               request_fun: request_fun
             )

    assert length(issues) == 101

    assert_receive {:request, first_request}
    assert first_request[:url] == "https://github.example.test/repos/octo/demo/issues"
    assert first_request[:params] == %{state: "open", per_page: 100, page: 1}

    assert Enum.any?(first_request[:headers], fn
             {"authorization", "Bearer env-token"} -> true
             _header -> false
           end)

    assert_receive {:request, second_request}
    assert second_request[:params] == %{state: "open", per_page: 100, page: 2}
  end

  test "uses the default GitHub API base for nil, blank, and linear endpoints" do
    for endpoint <- [nil, "", "https://api.linear.app/graphql"] do
      request_fun = fn opts ->
        send(self(), {:url, opts[:url]})
        {:ok, %{status: 404, body: %{}}}
      end

      assert {:ok, nil} =
               Client.get_issue(github_settings(endpoint: endpoint), "7", request_fun: request_fun)

      assert_receive {:url, "https://api.github.com/repos/octo/demo/issues/7"}
    end
  end

  test "maps get_issue response statuses and request errors" do
    settings = github_settings()

    assert {:ok, %{"number" => 11}} =
             Client.get_issue(
               settings,
               "11",
               request_fun: fn _opts -> {:ok, %Req.Response{status: 200, body: %{"number" => 11}}} end
             )

    assert {:error, {:github_api_status, 500, %{"message" => "bad"}}} =
             Client.get_issue(
               settings,
               "11",
               request_fun: fn _opts -> {:ok, %{status: 500, body: %{"message" => "bad"}}} end
             )

    assert {:error, {:github_api_request, :timeout}} =
             Client.get_issue(settings, "11", request_fun: fn _opts -> {:error, :timeout} end)
  end

  test "default-arity wrappers use the configured request function" do
    previous_request_fun = Application.get_env(:symphony_elixir, :github_client_request_fun)

    on_exit(fn ->
      if is_nil(previous_request_fun) do
        Application.delete_env(:symphony_elixir, :github_client_request_fun)
      else
        Application.put_env(:symphony_elixir, :github_client_request_fun, previous_request_fun)
      end
    end)

    Application.put_env(:symphony_elixir, :github_client_request_fun, fn opts ->
      case {opts[:method], opts[:url]} do
        {:get, "https://api.github.com/repos/octo/demo/issues"} ->
          {:ok, %{status: 200, body: []}}

        {:get, "https://api.github.com/repos/octo/demo/issues/7"} ->
          {:ok, %{status: 404, body: %{}}}

        {:get, "https://api.github.com/repos/octo/demo/issues/7/comments"} ->
          {:ok, %{status: 200, body: []}}

        {:post, "https://api.github.com/repos/octo/demo/issues/7/comments"} ->
          {:ok, %{status: 201, body: %{"id" => 1}}}

        {:patch, "https://api.github.com/repos/octo/demo/issues/comments/9"} ->
          {:ok, %{status: 200, body: %{"id" => 9}}}

        {:patch, "https://api.github.com/repos/octo/demo/issues/7"} ->
          {:ok, %{status: 200, body: %{"number" => 7}}}
      end
    end)

    settings = github_settings()

    assert {:ok, []} = Client.list_issues(settings, "open")
    assert {:ok, nil} = Client.get_issue(settings, "7")
    assert {:ok, []} = Client.list_issue_comments(settings, "7")
    assert {:ok, %{"id" => 1}} = Client.create_issue_comment(settings, "7", "hello")
    assert {:ok, %{"id" => 9}} = Client.update_issue_comment(settings, 9, "updated")
    assert {:ok, %{"number" => 7}} = Client.update_issue(settings, "7", %{state: "closed"})
  end

  test "list_issue_comments paginates and reports errors" do
    settings = github_settings()

    request_fun = fn opts ->
      send(self(), {:comments_request, opts})

      if opts[:params][:page] == 1 do
        {:ok,
         %{
           status: 200,
           body: for(comment_id <- 1..100, do: %{"id" => comment_id, "body" => "note"})
         }}
      else
        {:ok, %{status: 200, body: [%{"id" => 101, "body" => "done"}]}}
      end
    end

    assert {:ok, comments} = Client.list_issue_comments(settings, "7", request_fun: request_fun)
    assert length(comments) == 101

    assert_receive {:comments_request, first_request}
    assert first_request[:url] == "https://api.github.com/repos/octo/demo/issues/7/comments"
    assert first_request[:params] == %{per_page: 100, page: 1}

    assert_receive {:comments_request, second_request}
    assert second_request[:params] == %{per_page: 100, page: 2}

    assert {:error, {:github_api_status, 500, %{}}} =
             Client.list_issue_comments(
               settings,
               "7",
               request_fun: fn _opts -> {:ok, %{status: 500, body: %{}}} end
             )

    assert {:error, {:github_api_request, :closed}} =
             Client.list_issue_comments(settings, "7", request_fun: fn _opts -> {:error, :closed} end)
  end

  test "list_issues reports status and request failures" do
    settings = github_settings()

    assert {:error, {:github_api_status, 500, %{}}} =
             Client.list_issues(
               settings,
               "open",
               request_fun: fn _opts -> {:ok, %{status: 500, body: %{}}} end
             )

    assert {:error, {:github_api_request, :closed}} =
             Client.list_issues(settings, "open", request_fun: fn _opts -> {:error, :closed} end)
  end

  test "issue comment and issue mutations support success, status, and request errors" do
    settings = github_settings()

    assert {:ok, %{"id" => 1}} =
             Client.create_issue_comment(
               settings,
               "7",
               "hello",
               request_fun: fn opts ->
                 send(self(), {:create_request, opts})
                 {:ok, %Req.Response{status: 201, body: %{"id" => 1}}}
               end
             )

    assert_receive {:create_request, create_request}
    assert create_request[:json] == %{body: "hello"}

    assert {:error, {:github_api_status, 422, %{"message" => "bad"}}} =
             Client.create_issue_comment(
               settings,
               "7",
               "hello",
               request_fun: fn _opts -> {:ok, %{status: 422, body: %{"message" => "bad"}}} end
             )

    assert {:error, {:github_api_request, :denied}} =
             Client.create_issue_comment(settings, "7", "hello", request_fun: fn _opts -> {:error, :denied} end)

    assert {:ok, %{"id" => 9}} =
             Client.update_issue_comment(
               settings,
               9,
               "edit",
               request_fun: fn opts ->
                 send(self(), {:update_integer_request, opts})
                 {:ok, %{status: 200, body: %{"id" => 9}}}
               end
             )

    assert_receive {:update_integer_request, update_integer_request}
    assert update_integer_request[:url] == "https://api.github.com/repos/octo/demo/issues/comments/9"
    assert update_integer_request[:json] == %{body: "edit"}

    assert {:ok, %{"id" => "comment-2"}} =
             Client.update_issue_comment(
               settings,
               "comment-2",
               "edit",
               request_fun: fn opts ->
                 send(self(), {:update_string_request, opts})
                 {:ok, %Req.Response{status: 201, body: %{"id" => "comment-2"}}}
               end
             )

    assert_receive {:update_string_request, update_string_request}
    assert update_string_request[:url] == "https://api.github.com/repos/octo/demo/issues/comments/comment-2"

    assert {:error, {:github_api_status, 409, %{}}} =
             Client.update_issue_comment(
               settings,
               "comment-2",
               "edit",
               request_fun: fn _opts -> {:ok, %{status: 409, body: %{}}} end
             )

    assert {:error, {:github_api_request, :refused}} =
             Client.update_issue_comment(
               settings,
               "comment-2",
               "edit",
               request_fun: fn _opts -> {:error, :refused} end
             )

    assert {:ok, %{"number" => 8}} =
             Client.update_issue(
               settings,
               "8",
               %{state: "closed"},
               request_fun: fn opts ->
                 send(self(), {:issue_request, opts})
                 {:ok, %Req.Response{status: 200, body: %{"number" => 8}}}
               end
             )

    assert_receive {:issue_request, issue_request}
    assert issue_request[:url] == "https://api.github.com/repos/octo/demo/issues/8"
    assert issue_request[:json] == %{state: "closed"}

    assert {:error, {:github_api_status, 400, %{}}} =
             Client.update_issue(
               settings,
               "8",
               %{state: "closed"},
               request_fun: fn _opts -> {:ok, %{status: 400, body: %{}}} end
             )

    assert {:error, {:github_api_request, :reset}} =
             Client.update_issue(
               settings,
               "8",
               %{state: "closed"},
               request_fun: fn _opts -> {:error, :reset} end
             )
  end

  defp github_settings(overrides \\ []) do
    tracker =
      struct!(
        Schema.Tracker,
        Keyword.merge(
          [
            kind: "github",
            api_token: "github-token",
            endpoint: nil,
            repo: "octo/demo",
            active_states: ["Todo", "In Progress"],
            terminal_states: ["Done", "Closed"]
          ],
          overrides
        )
      )

    %Schema{tracker: tracker}
  end
end
