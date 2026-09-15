# kotlinx.serialization keeps its generated serializers via companion objects.
-keepattributes *Annotation*, InnerClasses
-dontnote kotlinx.serialization.**
-keepclassmembers class com.superbryn.phonebridge.** {
    *** Companion;
}
-keepclasseswithmembers class com.superbryn.phonebridge.** {
    kotlinx.serialization.KSerializer serializer(...);
}
