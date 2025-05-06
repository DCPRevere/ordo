# Ordo Project: Runtime Architecture Specification

## 1. Overview

This document outlines the runtime architecture for the **Ordo** software project. The core of the system involves processing jobs scheduled via an API and triggered based on time, using an event-sourcing approach with KurrentDB.

* **Event Store:** KurrentDB 25 serves as the central event store. All streams are prefixed with `ordo-`. Job-related events (e.g., `JobScheduled`, `JobTriggered`, `JobExecuted`, `JobCancelled`) are written to specific job streams within KurrentDB. The stream naming convention for individual job lifecycles follows the pattern `ordo-job-<guid>`. Other system or aggregate streams will also follow the `ordo-` prefix convention (e.g., `ordo-timekeeper-projection`).
* **Executor Group:** A logical concept representing the pool of **Ordo.Executor** instances. KurrentDB subscriptions manage the distribution of relevant events (like `JobTriggered`) to these instances.

## 2. Technology Stack

* **Language:** F#
* **Platform:** .NET 9
* **Database / Event Store:** KurrentDB 25

## 3. Architecture

Ordo utilizes a distributed, microservices-based architecture built on event sourcing principles with KurrentDB.

**System Components:**

1.  **Ordo.Core (Shared Library):**
    * **Responsibility:** A shared F# library containing common domain logic and types. Crucially, it includes the logic required to rebuild the state of a job entity by processing a sequence of its events read from a KurrentDB stream (e.g., `ordo-job-<guid>`). This ensures consistent state interpretation across different services.
    * **Usage:** Referenced by `Ordo.Timekeeper` (for its projection) and `Ordo.Executor` (to understand the job state before execution).

2.  **Ordo.Api:**
    * **Responsibility:** Provides an HTTP API for clients to schedule new jobs and request job cancellations. Upon receiving a valid scheduling request, it writes a `JobScheduled` event to the corresponding `ordo-job-<guid>` stream in KurrentDB. Cancellation requests would similarly result in a `JobCancelled` event being written to the appropriate stream.
    * **Scalability:** A single instance of this service is required. High availability, if needed, should be managed through infrastructure mechanisms (e.g., automated restarts or orchestration platform features) rather than multiple load-balanced instances.

3.  **Ordo.Timekeeper:**
    * **Responsibility:** Subscribes to all event streams from KurrentDB. It uses a synchronizer component, leveraging logic from **Ordo.Core**, to build and maintain an in-memory projection of the current state of all active, scheduled jobs based on the events it reads. On a regular tick (timer interval), it identifies jobs whose scheduled time has arrived and attempts to write a `JobTriggered` event to the respective `ordo-job-<guid>` stream in KurrentDB.
    * **Concurrency Control:** It uses KurrentDB's expected version feature when writing the `JobTriggered` event. If the write fails because the stream version is different than expected, it indicates that another **Ordo.Timekeeper** instance has already successfully triggered the job. In this case, the current instance simply ignores the failure and moves on to the next job, ensuring exactly-once triggering without requiring leader election.
    * **Scalability:** Multiple instances can run concurrently for high availability and potentially distributing the projection load (depending on the subscription strategy). The optimistic concurrency check on write guarantees correctness.

4.  **Ordo.Executor:**
    * **Responsibility:** Instances of this service subscribe to relevant event streams or categories in KurrentDB, specifically listening for `JobTriggered` events. Upon receiving a trigger event, an Executor instance reads all events from the corresponding `ordo-job-<guid>` stream. It uses the logic within **Ordo.Core** to rebuild the current state of the job entity. Based on this state, it executes the required processing logic. After successful execution, it writes a `JobExecuted` event back to the `ordo-job-<guid>` stream in KurrentDB. Finally, it acknowledges (ACKs) the `JobTriggered` event to the KurrentDB subscription to prevent reprocessing. Error handling (e.g., writing a `JobFailed` event, NACKing the message) would occur if execution fails.
    * **Scalability:** **Designed to run as multiple instances.** KurrentDB subscriptions (e.g., persistent subscriptions competing for events) distribute the `JobTriggered` events among the available **Ordo.Executor** instances. This allows for parallel processing, horizontal scaling (adding more instances to increase throughput), and high availability.

## 4. Running Multiple Instances

* **Ordo.Api:** No, runs as a single instance.
* **Ordo.Timekeeper:** Yes, runs as multiple active instances, relying on optimistic concurrency for safe concurrent operation.
* **Ordo.Executor:** **Yes, runs as multiple instances.** This leverages KurrentDB's subscription capabilities for parallel processing, scalability, and high availability.

## 5. Summary

The architecture for Ordo consists of:

* A shared **Ordo.Core** library (F#/.NET 9) for job entity state reconstruction.
* A single-instance **Ordo.Api** service (F#/.NET 9) accepting HTTP requests and writing `JobScheduled` events to KurrentDB 25 streams (prefixed `ordo-job-<guid>`).
* Multiple concurrently running **Ordo.Timekeeper** services (F#/.NET 9) maintaining an in-memory state from KurrentDB events (using **Ordo.Core**) and writing `JobTriggered` events at scheduled times using optimistic concurrency checks.
* Multiple instances of the **Ordo.Executor** service (F#/.NET 9) subscribing to `JobTriggered` events, rebuilding job state using **Ordo.Core**, executing the job logic, writing a `JobExecuted` event back to KurrentDB, and acknowledging the trigger event.

This event-sourced approach using F#, .NET 9, and KurrentDB provides a robust, scalable, and traceable system for job processing, leveraging optimistic concurrency for the Timekeeper component.