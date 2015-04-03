﻿namespace MBrace

open System
open System.Collections
open System.Collections.Generic
open System.Runtime.Serialization
open System.Text
open System.IO

open MBrace
open MBrace.Store
open MBrace.Continuation

#nowarn "444"

// sketch of cloud collection design; provisional design

/// Represents an abstract, distributed collection of values.
type ICloudCollection<'T> =
    /// Computes the element count for the collection.
    abstract Count : Local<int64>
    /// Gets an enumeration of all elements in the collection
    abstract ToEnumerable : unit -> Local<seq<'T>>

/// A cloud collection that comprises of a fixed number of partitions.
type IPartitionedCollection<'T> =
    inherit ICloudCollection<'T>
    /// Gets the partition count of the collection.
    abstract PartitionCount : Local<int>
    /// Gets all partitions for the collection.
    abstract GetPartitions : unit -> Local<ICloudCollection<'T> []>

/// A cloud collection that can be partitioned into smaller collections of provided size.
type IPartitionableCollection<'T> =
    inherit ICloudCollection<'T>
    /// Partitions the collection into collections of given count
    abstract GetPartitions : partitionCount:int -> Local<ICloudCollection<'T> []>