defmodule SymphonyElixir.GitHub.Client do
  @moduledoc """
  Thin GitHub REST client for tracker adapter operations.
  """

  alias SymphonyElixir.Config.Schema

  @default_api_base "https://api.github.com"
  @page_size 100

  @spec list_issues(Schema.t(), String.t(), keyword()) :: {:ok, [map()]} | {:error, term()}
  def list_issues(%Schema{} = settings, state, opts \\ []) when is_binary(state) do
    paginate_issues(settings, state, 1, opts, [])
  end

  @spec get_issue(Schema.t(), String.t(), keyword()) :: {:ok, map() | nil} | {:error, term()}
  def get_issue(%Schema{} = settings, issue_number, opts \\ []) when is_binary(issue_number) do
    case request(settings, :get, issue_path(settings, issue_number), opts) do
      {:ok, %{status: 200, body: %{} = issue}} ->
        {:ok, issue}

      {:ok, %{status: 404}} ->
        {:ok, nil}

      {:ok, %{status: status, body: body}} ->
        {:error, {:github_api_status, status, body}}

      {:error, reason} ->
        {:error, reason}
    end
  end

  @spec list_issue_comments(Schema.t(), String.t(), keyword()) :: {:ok, [map()]} | {:error, term()}
  def list_issue_comments(%Schema{} = settings, issue_number, opts \\ [])
      when is_binary(issue_number) do
    paginate_comments(settings, issue_number, 1, opts, [])
  end

  @spec create_issue_comment(Schema.t(), String.t(), String.t(), keyword()) :: {:ok, map()} | {:error, term()}
  def create_issue_comment(%Schema{} = settings, issue_number, body, opts \\ [])
      when is_binary(issue_number) and is_binary(body) do
    case request(
           settings,
           :post,
           issue_comments_path(settings, issue_number),
           Keyword.put(opts, :json, %{body: body})
         ) do
      {:ok, %{status: status, body: %{} = comment}} when status in [200, 201] ->
        {:ok, comment}

      {:ok, %{status: status, body: response_body}} ->
        {:error, {:github_api_status, status, response_body}}

      {:error, reason} ->
        {:error, reason}
    end
  end

  @spec update_issue_comment(Schema.t(), term(), String.t(), keyword()) :: {:ok, map()} | {:error, term()}
  def update_issue_comment(%Schema{} = settings, comment_id, body, opts \\ [])
      when (is_binary(comment_id) or is_integer(comment_id)) and is_binary(body) do
    case request(
           settings,
           :patch,
           comment_path(settings, comment_id),
           Keyword.put(opts, :json, %{body: body})
         ) do
      {:ok, %{status: status, body: %{} = comment}} when status in [200, 201] ->
        {:ok, comment}

      {:ok, %{status: status, body: response_body}} ->
        {:error, {:github_api_status, status, response_body}}

      {:error, reason} ->
        {:error, reason}
    end
  end

  @spec update_issue(Schema.t(), String.t(), map(), keyword()) :: {:ok, map()} | {:error, term()}
  def update_issue(%Schema{} = settings, issue_number, attrs, opts \\ [])
      when is_binary(issue_number) and is_map(attrs) do
    case request(
           settings,
           :patch,
           issue_path(settings, issue_number),
           Keyword.put(opts, :json, attrs)
         ) do
      {:ok, %{status: status, body: %{} = issue}} when status in [200, 201] ->
        {:ok, issue}

      {:ok, %{status: status, body: response_body}} ->
        {:error, {:github_api_status, status, response_body}}

      {:error, reason} ->
        {:error, reason}
    end
  end

  defp paginate_issues(settings, state, page, opts, acc) do
    case request(
           settings,
           :get,
           issues_path(settings),
           Keyword.put(opts, :params, %{state: state, per_page: @page_size, page: page})
         ) do
      {:ok, %{status: 200, body: issues}} when is_list(issues) ->
        merged = acc ++ issues

        if length(issues) < @page_size do
          {:ok, merged}
        else
          paginate_issues(settings, state, page + 1, opts, merged)
        end

      {:ok, %{status: status, body: body}} ->
        {:error, {:github_api_status, status, body}}

      {:error, reason} ->
        {:error, reason}
    end
  end

  defp paginate_comments(settings, issue_number, page, opts, acc) do
    case request(
           settings,
           :get,
           issue_comments_path(settings, issue_number),
           Keyword.put(opts, :params, %{per_page: @page_size, page: page})
         ) do
      {:ok, %{status: 200, body: comments}} when is_list(comments) ->
        merged = acc ++ comments

        if length(comments) < @page_size do
          {:ok, merged}
        else
          paginate_comments(settings, issue_number, page + 1, opts, merged)
        end

      {:ok, %{status: status, body: body}} ->
        {:error, {:github_api_status, status, body}}

      {:error, reason} ->
        {:error, reason}
    end
  end

  defp request(%Schema{} = settings, method, path, opts) when is_atom(method) do
    request_fun =
      Keyword.get(
        opts,
        :request_fun,
        Application.get_env(:symphony_elixir, :github_client_request_fun, &Req.request/1)
      )

    params = Keyword.get(opts, :params)
    json = Keyword.get(opts, :json)

    request_options =
      [
        method: method,
        url: build_url(settings, path),
        headers: headers(settings),
        connect_options: [timeout: 30_000]
      ]
      |> maybe_put(:params, params)
      |> maybe_put(:json, json)

    case request_fun.(request_options) do
      {:ok, response} ->
        {:ok, normalize_response(response)}

      {:error, reason} ->
        {:error, {:github_api_request, reason}}
    end
  end

  defp normalize_response(%Req.Response{status: status, body: body}) do
    %{status: status, body: body}
  end

  defp normalize_response(%{status: status, body: body}) do
    %{status: status, body: body}
  end

  defp build_url(%Schema{tracker: tracker}, path) do
    base_url =
      case tracker.endpoint do
        nil -> @default_api_base
        "" -> @default_api_base
        "https://api.linear.app/graphql" -> @default_api_base
        endpoint -> String.trim_trailing(endpoint, "/")
      end

    base_url <> path
  end

  defp headers(%Schema{tracker: tracker}) do
    token =
      case tracker.api_token do
        value when is_binary(value) and value != "" -> value
        _ -> System.get_env("GITHUB_TOKEN") || ""
      end

    [
      {"accept", "application/vnd.github+json"},
      {"authorization", "Bearer #{token}"},
      {"user-agent", "symphony-elixir"},
      {"x-github-api-version", "2022-11-28"}
    ]
  end

  defp issues_path(%Schema{tracker: tracker}), do: "/repos/#{tracker.repo}/issues"
  defp issue_path(%Schema{tracker: tracker}, issue_number), do: "/repos/#{tracker.repo}/issues/#{issue_number}"

  defp issue_comments_path(%Schema{tracker: tracker}, issue_number) do
    "/repos/#{tracker.repo}/issues/#{issue_number}/comments"
  end

  defp comment_path(%Schema{tracker: tracker}, comment_id) when is_integer(comment_id) do
    "/repos/#{tracker.repo}/issues/comments/#{comment_id}"
  end

  defp comment_path(%Schema{tracker: tracker}, comment_id) do
    "/repos/#{tracker.repo}/issues/comments/#{comment_id}"
  end

  defp maybe_put(options, _key, nil), do: options
  defp maybe_put(options, key, value), do: Keyword.put(options, key, value)
end
