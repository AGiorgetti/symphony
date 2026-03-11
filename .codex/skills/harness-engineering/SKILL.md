---
name: harness-engineering
description: Design and tighten agent-facing software harnesses, repo guardrails, and task boundaries so Codex or other coding agents can execute work with less ambiguity and more reliable feedback. Use when creating or improving AGENTS.md files, automation loops, repository instructions, validation workflows, task decomposition, or evaluation-driven engineering processes for agentic coding.
---

# Harness Engineering

## Overview

Treat the repository as a harness for the model, not just a container for source code. Reduce ambiguity, expose fast feedback loops, and make the intended path easier than the wrong path.

Read [references/harness-principles.md](references/harness-principles.md) when you need the article-derived rationale, design heuristics, or a deeper checklist.

## Workflow

### 1. Define the task boundary

Translate the user request into a narrow, testable contract:

- State the input, expected output, and constraints.
- Separate blocking work from sidecar work.
- Prefer artifacts the agent can verify locally: files, tests, reports, comments, or generated patches.
- If the request is broad, split it into subproblems with explicit ownership and stopping conditions.

### 2. Inspect the current harness

Check what the repo already provides for an agent:

- `AGENTS.md`, `.codex/skills/`, automation files, task templates, and existing scripts
- lint, test, typecheck, or build commands
- CI configuration, PR templates, or issue templates
- any places where the agent must infer hidden conventions

Look for friction:

- instructions spread across too many files
- missing acceptance criteria
- slow or noisy validation
- tasks that require repeated rediscovery
- outputs that are hard to diff or review

### 3. Tighten the loop

Prefer the smallest change that improves execution reliability:

- add or sharpen repo instructions
- create a focused skill for recurring workflows
- add a deterministic script instead of re-explaining a manual procedure
- add a cheap validation command before expensive end-to-end checks
- restructure prompts so the deliverable shape is explicit

Bias toward fast local feedback. An agent should be able to tell quickly whether it is getting warmer or colder.

### 4. Make success legible

When you change the harness, make the expected path obvious:

- name files and scripts after the task they support
- keep instructions imperative and concrete
- document triggers in frontmatter or top-level metadata
- use checklists only when they reduce omission risk
- encode recurring review criteria into scripts, templates, or comments where possible

### 5. Verify the harness itself

Test the new setup as if you were a fresh agent:

- Can you discover the workflow from the repo?
- Can you run the validation path without extra tribal knowledge?
- Can you tell what "done" means before making changes?
- Can a reviewer inspect the output quickly?

If the answer to any of these is no, the harness is still carrying too much implicit context.

## Common Deliverables

Produce one or more of these, depending on the gap:

- a clearer `AGENTS.md` section
- a new or revised Codex skill
- a script that standardizes a fragile workflow
- a lightweight evaluation or smoke-test command
- a task template with explicit inputs, outputs, and review criteria
- a short design note explaining tradeoffs and validation

## Operating Rules

- Prefer removing ambiguity over adding prose.
- Prefer concrete examples over abstract principles.
- Prefer local verification over promises about future CI.
- Prefer reusable scaffolding over one-off prompt heroics.
- Prefer task decomposition that mirrors how work will actually be reviewed.

## Reference Use

Use [references/harness-principles.md](references/harness-principles.md) for:

- a distilled summary of the OpenAI "Harness Engineering" article
- repository-level checklist items
- guidance on evaluations, tools, decomposition, and context compression
