defmodule SymphonyElixir.Tracker.Registry do
  @moduledoc """
  Resolves tracker adapter modules from runtime configuration.
  """

  alias SymphonyElixir.Config.Schema

  @spec resolve(Schema.t()) :: {:ok, module()} | {:error, term()}
  def resolve(%Schema{tracker: tracker}) do
    tracker
    |> resolve_module()
    |> ensure_loaded_adapter()
  end

  @spec resolve_module(map()) :: {:ok, module()} | {:error, term()}
  def resolve_module(%{module: module_name} = tracker) when is_binary(module_name) do
    case String.trim(module_name) do
      "" ->
        resolve_module(Map.put(tracker, :module, nil))

      trimmed ->
        try do
          {:ok, Module.safe_concat(String.split(trimmed, "."))}
        rescue
          ArgumentError ->
            {:error, {:invalid_tracker_module, module_name}}
        end
    end
  end

  def resolve_module(%{kind: "linear"}), do: {:ok, SymphonyElixir.Tracker.Linear}
  def resolve_module(%{kind: "github"}), do: {:ok, SymphonyElixir.Tracker.GitHub}
  def resolve_module(%{kind: "memory"}), do: {:ok, SymphonyElixir.Tracker.Memory}
  def resolve_module(%{kind: nil}), do: {:error, :missing_tracker_kind}
  def resolve_module(%{kind: kind}), do: {:error, {:unsupported_tracker_kind, kind}}
  def resolve_module(_tracker), do: {:error, :missing_tracker_kind}

  defp ensure_loaded_adapter({:ok, module}) do
    case Code.ensure_loaded(module) do
      {:module, ^module} -> {:ok, module}
      {:error, reason} -> {:error, {:tracker_module_unavailable, module, reason}}
    end
  end

  defp ensure_loaded_adapter({:error, reason}), do: {:error, reason}
end
